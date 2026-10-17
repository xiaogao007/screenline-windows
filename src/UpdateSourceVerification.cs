using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Screenline
{
    // The signing keys are ephemeral CSP keys and never leave memory. Every HTTP
    // response below comes from this handler; synthetic MZ bytes are never executed.
    internal static class UpdateSourceVerification
    {
        private const string Tag = "v9.1.0";
        private const string InstallerName = "Screenline-v9.1.0-windows-x64-setup.exe";
        private const string BaseUrl = "https://screenline-fixture.oss-cn-hangzhou.aliyuncs.com/screenline/";
        private const string LatestUrl = BaseUrl + "stable/latest.json";
        private const string InstallerUrl = BaseUrl + "releases/" + Tag + "/" + InstallerName;
        private const string GithubLatest = "https://api.github.com/repos/xiaogao007/screenline-windows/releases/latest";
        private const string GithubBase = "https://github.com/xiaogao007/screenline-windows/releases/download/" + Tag + "/";
        private static readonly byte[] installerBytes = MakeInstallerBytes();
        private static readonly string installerHash = Hash(installerBytes);
        private static int fixtureId;

        internal static void Run(string sandbox, Action<bool, string> check)
        {
            Task.Run(delegate
            {
                string root = Path.Combine(sandbox, "m");
                Directory.CreateDirectory(root);
                using (var signer = NewSigningKey())
                    RunTests(root, signer, check);
            }).GetAwaiter().GetResult();
        }

        private static RSACryptoServiceProvider NewSigningKey(int bits = 2048)
        {
            return new RSACryptoServiceProvider(bits, new CspParameters
            {
                ProviderType = 24,
                Flags = CspProviderFlags.CreateEphemeralKey
            }) { PersistKeyInCsp = false };
        }

        private static void RunTests(string root, RSACryptoServiceProvider signer, Action<bool, string> check)
        {
            string publicKey = signer.ToXmlString(false);
            UpdateSource source = UpdateSource.FromConfigJson(Config(BaseUrl, publicKey));
            string payload = Payload(Tag, InstallerName, installerBytes.Length, installerHash);
            string envelope = Signed(signer, payload);

            string configDirectory = Path.Combine(root, "c");
            Directory.CreateDirectory(configDirectory);
            check(UpdateSource.LoadOptional(configDirectory) == null &&
                UpdateSource.FromConfigJson(null) == null && UpdateSource.FromConfigJson(" ") == null &&
                UpdateSource.FromConfigJson(Config("", "")) == null,
                "A missing or explicitly blank domestic update configuration leaves the source disabled");
            using (var disabled = new Fixture(root, null, envelope))
            {
                ReleaseUpdate update = disabled.Check("9.0.0");
                check(update != null && disabled.Handler.Requests.Count == 2 &&
                    disabled.Handler.Requests[0].AbsoluteUri == GithubLatest &&
                    disabled.Handler.Requests.All(delegate(Uri uri) { return uri.Host != new Uri(BaseUrl).Host; }),
                    "Without a configured domestic source the updater only uses the existing GitHub release and checksum requests");
            }

            string[] invalidUrls = new string[]
            {
                "http://screenline-fixture.oss-cn-hangzhou.aliyuncs.com/screenline/",
                "https://user@screenline-fixture.oss-cn-hangzhou.aliyuncs.com/screenline/",
                "https://screenline-fixture.oss-cn-hangzhou.aliyuncs.com:444/screenline/",
                BaseUrl + "?token=fixture",
                BaseUrl + "#fragment"
            };
            check(invalidUrls.All(delegate(string url) { return Rejected(delegate { UpdateSource.FromConfigJson(Config(url, publicKey)); }); }),
                "Domestic source configuration rejects HTTP, credentials, unusual ports, query strings, and fragments before any request");
            check(Rejected(delegate { UpdateSource.FromConfigJson("{ invalid json"); }) &&
                Rejected(delegate { UpdateSource.FromConfigJson(Config(BaseUrl, "not an RSA public key")); }) &&
                Rejected(delegate { UpdateSource.FromConfigJson(Config(BaseUrl, "")); }) &&
                Rejected(delegate { UpdateSource.FromConfigJson(Config("", publicKey)); }),
                "Malformed or incomplete domestic source configuration is rejected without trusting a download host");
            using (var weakKey = NewSigningKey(1024))
                check(Rejected(delegate { UpdateSource.FromConfigJson(Config(BaseUrl, weakKey.ToXmlString(false))); }) &&
                    Rejected(delegate { UpdateSource.FromConfigJson(Config(BaseUrl, signer.ToXmlString(true))); }),
                    "Domestic configuration requires a strong RSA public key and refuses private key material");
            string config = Config(BaseUrl, publicKey);
            check(Rejected(delegate { UpdateSource.FromConfigJson("{\"BaseUrl\":\"" + BaseUrl + "\"," + config.Substring(1)); }) &&
                Rejected(delegate { UpdateSource.FromConfigJson("{\"Unknown\":\"fixture\"," + config.Substring(1)); }) &&
                Rejected(delegate { UpdateSource.FromConfigJson("{\"__type\":\"fixture\"," + config.Substring(1)); }),
                "Domestic configuration rejects duplicate or unknown JSON fields and serializer type metadata");
            File.WriteAllText(Path.Combine(configDirectory, "update-source.json"), "{ invalid json", Encoding.UTF8);
            check(UpdateSource.LoadOptional(configDirectory) == null,
                "A malformed optional deployment configuration safely preserves GitHub update availability");
            File.WriteAllText(Path.Combine(configDirectory, "update-source.json"), Config(BaseUrl, publicKey), Encoding.UTF8);
            using (var configured = new Fixture(root, UpdateSource.LoadOptional(configDirectory), envelope))
                check(configured.Check("9.0.0") != null && configured.Handler.Requests.Count == 1 &&
                    configured.Handler.Requests[0].AbsoluteUri == LatestUrl,
                    "A deployed public-key configuration resolves its signed manifest through the isolated domestic source");

            using (var valid = new Fixture(root, source, envelope))
            {
                ReleaseUpdate update = valid.Check("9.0.0");
                check(update != null && update.Tag == Tag && update.InstallerName == InstallerName &&
                    update.InstallerSize == installerBytes.Length && update.InstallerDigest == installerHash &&
                    update.InstallerUri.AbsoluteUri == InstallerUrl && update.ReleaseNotes == "离线签名更新测试：保留用户数据。" &&
                    valid.Handler.Requests.Count == 1 && valid.Handler.Requests[0].AbsoluteUri == LatestUrl,
                    "A valid RSA-SHA256 manifest provides its exact installer, checksum, and Chinese notes without contacting GitHub");
            }
            using (var current = new Fixture(root, source, envelope))
                check(current.Check("9.1.0") == null && current.Check("9.2.0") == null &&
                    current.Handler.Requests.Count == 2 && current.Handler.Requests.All(delegate(Uri uri) { return uri.AbsoluteUri == LatestUrl; }),
                    "An authenticated domestic release equal to or older than the installed version ends the check without GitHub fallback");

            byte[] signature = signer.SignData(Encoding.UTF8.GetBytes(payload), CryptoConfig.MapNameToOID("SHA256"));
            signature[0] ^= 1;
            VerifyFallback(root, source, Envelope(payload, signature), check,
                "An invalid RSA signature never offers a domestic installer and falls back to the verified GitHub release");
            string altered = payload.Replace(installerHash, new string('0', 64));
            VerifyFallback(root, source, Envelope(altered, signer.SignData(Encoding.UTF8.GetBytes(payload), CryptoConfig.MapNameToOID("SHA256"))), check,
                "Changing a signed payload after signing invalidates it before its installer can be offered");
            VerifyFallback(root, source, payload, check,
                "An unsigned version payload is ignored and the updater falls back to GitHub");
            VerifyFallback(root, source, "{\"payload\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) + "\"}", check,
                "A manifest envelope without its signature is ignored before GitHub fallback");
            VerifyFallback(root, source, "{\"payload\":\"%%%\",\"signature\":\"%%%\"}", check,
                "Malformed base64 in a domestic manifest produces a controlled GitHub fallback");
            VerifyFallback(root, source, new string('x', 2 * 1024 * 1024), check,
                "An oversized domestic metadata response is bounded and falls back without parsing unbounded input");
            VerifyFallback(root, source, "{\"payload\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) + "\"," + envelope.Substring(1), check,
                "A manifest with duplicate envelope fields cannot select an ambiguous authenticated payload");
            VerifyFallback(root, source, "{\"__type\":\"fixture\"," + envelope.Substring(1), check,
                "Serializer type metadata cannot change the interpretation of a signed manifest envelope");

            string[] invalidPayloads = new string[]
            {
                payload.Replace("\"schema\":1", "\"schema\":2"),
                Payload("v9.1.0-rc.1", "Screenline-v9.1.0-rc.1-windows-x64-setup.exe", installerBytes.Length, installerHash),
                Payload("v9.01.0", "Screenline-v9.01.0-windows-x64-setup.exe", installerBytes.Length, installerHash),
                Payload(Tag, "../" + InstallerName, installerBytes.Length, installerHash),
                Payload(Tag, InstallerName, UpdateService.MaximumInstallerBytes + 1, installerHash),
                Payload(Tag, InstallerName, installerBytes.Length, "not-a-sha256"),
                "{\"sha256\":\"" + installerHash + "\"," + payload.Substring(1),
                "{\"__type\":\"fixture\"," + payload.Substring(1),
                payload.Replace("\"size\":" + installerBytes.Length, "\"size\":\"" + installerBytes.Length + "\"")
            };
            bool allRejected = true;
            foreach (string invalidPayload in invalidPayloads)
            {
                using (var invalid = new Fixture(root, source, Signed(signer, invalidPayload)))
                {
                    ReleaseUpdate update = invalid.Check("9.0.0");
                    allRejected &= update != null && update.InstallerUri.AbsoluteUri == GithubBase + InstallerName &&
                        invalid.Handler.Requests.Count == 3 && invalid.Handler.Requests[1].AbsoluteUri == GithubLatest;
                }
            }
            check(allRejected,
                "Even authenticated manifests require unambiguous typed fields, the supported schema, stable SemVer, exact installer name, bounded size, and SHA-256");

            using (var unavailable = new Fixture(root, source, envelope))
            {
                unavailable.Handler.MirrorOverride = delegate(HttpRequestMessage request, CancellationToken token)
                { throw new HttpRequestException("Synthetic domestic source unavailable"); };
                ReleaseUpdate update = unavailable.Check("9.0.0");
                check(update != null && update.InstallerUri.AbsoluteUri == GithubBase + InstallerName &&
                    unavailable.Handler.Requests.Count == 3 && unavailable.Handler.Requests[1].AbsoluteUri == GithubLatest,
                    "An unavailable domestic source falls back to GitHub and still requires GitHub installer checksums");
            }
            using (var canceled = new Fixture(root, source, envelope))
            using (var cancellation = new CancellationTokenSource())
            {
                canceled.Handler.MirrorOverride = delegate(HttpRequestMessage request, CancellationToken token)
                { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return null; };
                check(Canceled(delegate { canceled.Service.CheckAsync("9.0.0", cancellation.Token).GetAwaiter().GetResult(); }) &&
                    canceled.Handler.Requests.Count == 1 && canceled.Handler.Requests[0].AbsoluteUri == LatestUrl,
                    "Canceling an in-flight domestic check stops the operation without requesting the GitHub fallback");
            }
            using (var canceled = new Fixture(root, source, envelope))
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                check(Canceled(delegate { canceled.Service.CheckAsync("9.0.0", cancellation.Token).GetAwaiter().GetResult(); }) &&
                    canceled.Handler.Requests.Count == 0,
                    "An already-canceled domestic update check makes no request to either source");
            }
            using (var redirected = new Fixture(root, source, envelope))
            {
                redirected.Handler.MirrorOverride = delegate(HttpRequestMessage request, CancellationToken token)
                { return Redirect("https://foreign-fixture.invalid/latest.json"); };
                ReleaseUpdate update = redirected.Check("9.0.0");
                check(update != null && redirected.Handler.Requests.Count == 3 &&
                    redirected.Handler.Requests.All(delegate(Uri uri) { return uri.Host != "foreign-fixture.invalid"; }),
                    "A domestic manifest redirect to a foreign host is blocked before any request to that host");
            }

            VerifyDownloads(root, source, signer, envelope, check);
        }

        private static void VerifyDownloads(string root, UpdateSource source, RSACryptoServiceProvider signer,
            string envelope, Action<bool, string> check)
        {
            using (var valid = new Fixture(root, source, envelope))
            {
                ReleaseUpdate update = valid.Check("9.0.0");
                var progress = new RecordedProgress();
                string path = valid.Service.DownloadAsync(update, progress, CancellationToken.None).GetAwaiter().GetResult();
                UpdateService.VerifyInstallerFile(update, path);
                check(File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(installerBytes) &&
                    Path.GetFileName(path) == InstallerName && progress.Values.Count > 0 && progress.Values.Last() == 100 &&
                    valid.Handler.Requests.Count == 2 && valid.Handler.Requests[1].AbsoluteUri == InstallerUrl,
                    "A signed domestic installer downloads from its configured origin and passes size, MZ, and SHA-256 checks before it becomes runnable");
            }
            using (var corrupt = new Fixture(root, source, envelope))
            {
                ReleaseUpdate update = corrupt.Check("9.0.0");
                corrupt.Handler.DownloadBytes = (byte[])installerBytes.Clone();
                corrupt.Handler.DownloadBytes[corrupt.Handler.DownloadBytes.Length - 1] ^= 1;
                var progress = new RecordedProgress();
                check(Rejected(delegate { corrupt.Service.DownloadAsync(update, progress, CancellationToken.None).GetAwaiter().GetResult(); }) &&
                    !progress.Values.Contains(100) && NoDownloadFiles(corrupt.Root),
                    "A domestic installer with a wrong SHA-256 leaves neither an executable nor a partial download");
            }
            using (var truncated = new Fixture(root, source, envelope))
            {
                ReleaseUpdate update = truncated.Check("9.0.0");
                truncated.Handler.DownloadBytes = installerBytes.Take(installerBytes.Length / 2).ToArray();
                check(Rejected(delegate { truncated.Service.DownloadAsync(update, null, CancellationToken.None).GetAwaiter().GetResult(); }) && NoDownloadFiles(truncated.Root),
                    "A domestic installer whose response length differs from the signed size is rejected without a runnable file");
            }
            byte[] text = Encoding.UTF8.GetBytes("Synthetic non-executable fixture, never run this text.");
            using (var notExecutable = new Fixture(root, source, Signed(signer, Payload(Tag, InstallerName, text.Length, Hash(text)))))
            {
                notExecutable.Handler.DownloadBytes = text;
                ReleaseUpdate update = notExecutable.Check("9.0.0");
                check(Rejected(delegate { notExecutable.Service.DownloadAsync(update, null, CancellationToken.None).GetAwaiter().GetResult(); }) && NoDownloadFiles(notExecutable.Root),
                    "A domestic file with matching signed size and hash still needs the Windows executable signature");
            }
            using (var interrupted = new Fixture(root, source, envelope))
            using (var cancellation = new CancellationTokenSource())
            {
                ReleaseUpdate update = interrupted.Check("9.0.0");
                interrupted.Handler.DownloadStream = delegate { return new CancelingStream(installerBytes, cancellation); };
                check(Canceled(delegate { interrupted.Service.DownloadAsync(update, null, cancellation.Token).GetAwaiter().GetResult(); }) && NoDownloadFiles(interrupted.Root),
                    "Canceling a domestic installer transfer removes its partial file and produces no executable");
            }
            using (var redirected = new Fixture(root, source, envelope))
            {
                ReleaseUpdate update = redirected.Check("9.0.0");
                redirected.Handler.DownloadOverride = delegate(HttpRequestMessage request, CancellationToken token)
                { return Redirect("https://foreign-fixture.invalid/" + InstallerName); };
                check(Rejected(delegate { redirected.Service.DownloadAsync(update, null, CancellationToken.None).GetAwaiter().GetResult(); }) &&
                    redirected.Handler.Requests.Count == 2 && NoDownloadFiles(redirected.Root) &&
                    redirected.Handler.Requests.All(delegate(Uri uri) { return uri.Host != "foreign-fixture.invalid"; }),
                    "A domestic installer cannot redirect to an unconfigured host even when its original manifest was signed");
            }
            using (var redirected = new Fixture(root, source, envelope))
            {
                ReleaseUpdate update = redirected.Check("9.0.0");
                redirected.Handler.DownloadOverride = delegate(HttpRequestMessage request, CancellationToken token)
                { return Redirect(BaseUrl + "other/" + InstallerName); };
                check(Rejected(delegate { redirected.Service.DownloadAsync(update, null, CancellationToken.None).GetAwaiter().GetResult(); }) &&
                    redirected.Handler.Requests.Count == 2 && NoDownloadFiles(redirected.Root),
                    "A domestic installer redirect cannot substitute a different path on the configured origin");
            }
        }

        private static bool NoDownloadFiles(string root)
        {
            return !Directory.GetFiles(root, "*.exe", SearchOption.AllDirectories).Any() &&
                !Directory.GetFiles(root, "*.partial", SearchOption.AllDirectories).Any();
        }

        private static void VerifyFallback(string root, UpdateSource source, string envelope, Action<bool, string> check, string name)
        {
            using (var fixture = new Fixture(root, source, envelope))
            {
                ReleaseUpdate update = fixture.Check("9.0.0");
                check(update != null && update.InstallerUri.AbsoluteUri == GithubBase + InstallerName &&
                    fixture.Handler.Requests.Count == 3 && fixture.Handler.Requests[0].AbsoluteUri == LatestUrl &&
                    fixture.Handler.Requests[1].AbsoluteUri == GithubLatest, name);
            }
        }

        private static bool Rejected(Action action)
        {
            try { action(); return false; } catch (UpdateException) { return true; }
        }

        private static bool Canceled(Action action)
        {
            try { action(); return false; } catch (OperationCanceledException) { return true; }
        }

        private static string Config(string baseUrl, string publicKey)
        {
            return "{\"BaseUrl\":\"" + Escape(baseUrl) + "\",\"PublicKeyXml\":\"" + Escape(publicKey) + "\"}";
        }

        private static string Payload(string tag, string installer, long size, string hash)
        {
            return "{\"schema\":1,\"tag\":\"" + Escape(tag) + "\",\"installer\":\"" + Escape(installer) + "\",\"size\":" +
                size.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"sha256\":\"" + Escape(hash) +
                "\",\"notes\":\"离线签名更新测试：保留用户数据。\"}";
        }

        private static string Signed(RSACryptoServiceProvider key, string payload)
        {
            return Envelope(payload, key.SignData(Encoding.UTF8.GetBytes(payload), CryptoConfig.MapNameToOID("SHA256")));
        }

        private static string Envelope(string payload, byte[] signature)
        {
            return "{\"payload\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) + "\",\"signature\":\"" +
                Convert.ToBase64String(signature) + "\"}";
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static byte[] MakeInstallerBytes()
        {
            byte[] bytes = new byte[128]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            for (int i = 2; i < bytes.Length; i++) bytes[i] = (byte)(i * 29);
            return bytes;
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static HttpResponseMessage Redirect(string location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(location);
            return response;
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly string Root;
            internal readonly FakeHandler Handler;
            internal readonly UpdateService Service;
            internal Fixture(string root, UpdateSource source, string envelope)
            {
                Root = Path.Combine(root, Interlocked.Increment(ref fixtureId).ToString("x"));
                Directory.CreateDirectory(Root);
                Handler = new FakeHandler(envelope);
                Service = new UpdateService(Handler, Root, source);
            }
            internal ReleaseUpdate Check(string current)
            {
                return Service.CheckAsync(current, CancellationToken.None).GetAwaiter().GetResult();
            }
            public void Dispose() { Service.Dispose(); }
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly string envelope;
            internal readonly List<Uri> Requests = new List<Uri>();
            internal byte[] DownloadBytes = installerBytes;
            internal Func<Stream> DownloadStream;
            internal Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> MirrorOverride;
            internal Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> DownloadOverride;
            internal FakeHandler(string envelope) { this.envelope = envelope; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Requests.Add(request.RequestUri);
                if (request.RequestUri.AbsoluteUri == LatestUrl)
                {
                    if (MirrorOverride != null) return Task.FromResult(MirrorOverride(request, token));
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(envelope, Encoding.UTF8, "application/json") });
                }
                if (request.RequestUri.AbsoluteUri == GithubLatest)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GithubJson(), Encoding.UTF8, "application/json") });
                if (request.RequestUri.AbsoluteUri == GithubBase + "SHA256SUMS.txt")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(installerHash + "  " + InstallerName + "\n") });
                if (request.RequestUri.AbsoluteUri == InstallerUrl || request.RequestUri.AbsoluteUri == GithubBase + InstallerName)
                {
                    if (DownloadOverride != null) return Task.FromResult(DownloadOverride(request, token));
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = DownloadStream == null ?
                        (HttpContent)new ByteArrayContent(DownloadBytes) : new StreamContent(DownloadStream()) });
                }
                throw new InvalidOperationException("Unexpected request in offline domestic updater test: " + request.RequestUri.AbsoluteUri);
            }

            private static string GithubJson()
            {
                return "{\"tag_name\":\"" + Tag + "\",\"html_url\":\"https://github.com/xiaogao007/screenline-windows/releases/tag/" +
                    Tag + "\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"" + InstallerName +
                    "\",\"state\":\"uploaded\",\"size\":" + installerBytes.Length + ",\"digest\":\"sha256:" + installerHash +
                    "\",\"browser_download_url\":\"" + GithubBase + InstallerName + "\"},{\"name\":\"SHA256SUMS.txt\",\"state\":\"uploaded\",\"size\":128,\"browser_download_url\":\"" +
                    GithubBase + "SHA256SUMS.txt\"}]}";
            }
        }

        private sealed class RecordedProgress : IProgress<int>
        {
            internal readonly List<int> Values = new List<int>();
            public void Report(int value) { Values.Add(value); }
        }

        private sealed class CancelingStream : MemoryStream
        {
            private readonly CancellationTokenSource cancellation;
            internal CancelingStream(byte[] bytes, CancellationTokenSource source) : base(bytes, false) { cancellation = source; }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                int read = base.Read(buffer, offset, Math.Min(16, count));
                cancellation.Cancel();
                return Task.FromResult(read);
            }
        }
    }
}
