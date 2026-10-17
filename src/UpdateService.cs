using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Screenline
{
    public sealed class UpdateException : Exception
    {
        public UpdateException(string message) : base(message) { }
    }

    public sealed class ReleaseUpdate
    {
        public string Tag { get; private set; }
        public string Version { get; private set; }
        public Uri ReleaseUri { get; private set; }
        public string InstallerName { get; private set; }
        public long InstallerSize { get; private set; }
        public string InstallerDigest { get; internal set; }
        internal Uri InstallerUri { get; private set; }
        internal Uri ChecksumsUri { get; private set; }
        internal string ApiDigest { get; private set; }

        internal ReleaseUpdate(string tag, Uri releaseUri, string installerName, long installerSize,
            Uri installerUri, Uri checksumsUri, string apiDigest)
        {
            Tag = tag;
            Version = tag.Substring(1);
            ReleaseUri = releaseUri;
            InstallerName = installerName;
            InstallerSize = installerSize;
            InstallerUri = installerUri;
            ChecksumsUri = checksumsUri;
            ApiDigest = apiDigest;
        }
    }

    // Updates are user-initiated. This class only retrieves and verifies release files;
    // launching an installer is a separate, explicit decision in the update window.
    public sealed class UpdateService : IDisposable
    {
        internal const long MaximumInstallerBytes = 256L * 1024L * 1024L;
        private const int MaximumMetadataBytes = 1024 * 1024;
        private const int MaximumChecksumsBytes = 64 * 1024;
        private const string RepositoryPath = "/xiaogao007/screenline-windows";
        private const string LatestReleaseUrl = "https://api.github.com/repos" + RepositoryPath + "/releases/latest";
        private static readonly Regex HashPattern = new Regex("^[a-fA-F0-9]{64}\\z", RegexOptions.CultureInvariant);
        private static readonly Regex ChecksumLinePattern = new Regex("^([a-fA-F0-9]{64})[ \\t]+\\*?([^\\r\\n]+)\\z", RegexOptions.CultureInvariant);
        private readonly HttpClient client;
        private readonly string updatesRoot;

        public UpdateService()
            : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false },
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screenline", "Updates"))
        {
        }

        // The injected handler and root let the self-test exercise the entire download
        // lifecycle without network access or touching the user's picture library.
        internal UpdateService(HttpMessageHandler handler, string updatesRoot)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            if (String.IsNullOrWhiteSpace(updatesRoot)) throw new ArgumentException("An update directory is required.", "updatesRoot");
            this.updatesRoot = Path.GetFullPath(updatesRoot);
            client = new HttpClient(handler, true);
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Screenline-Windows/" + AppVersion.Current);
        }

        public async Task<ReleaseUpdate> CheckAsync(string currentVersion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    // Validate the local version before contacting GitHub.
                    SemanticVersion.Parse(currentVersion);
                    string json = await GetTextAsync(new Uri(LatestReleaseUrl), MaximumMetadataBytes, true, deadline.Token).ConfigureAwait(false);
                    if (json == null) return null; // No public stable release yet (HTTP 404).
                    ReleaseUpdate release = ParseLatestRelease(json, currentVersion);
                    if (release == null) return null;
                    string checksums = await GetTextAsync(release.ChecksumsUri, MaximumChecksumsBytes, false, deadline.Token).ConfigureAwait(false);
                    release.InstallerDigest = ParseInstallerChecksum(checksums, release.InstallerName);
                    if (release.ApiDigest != null && !String.Equals(release.InstallerDigest, release.ApiDigest, StringComparison.OrdinalIgnoreCase))
                        throw new UpdateException("发布文件的校验值不一致，请稍后重试或前往 GitHub Release 查看。");
                    return release;
                }
                catch (UpdateException) { throw; }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new UpdateException("检查更新超时，请检查网络后重试。");
                }
                catch (Exception ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested) throw new UpdateException("检查更新超时，请检查网络后重试。");
                    if (IsTransferError(ex)) throw new UpdateException("暂时无法连接 GitHub，请检查网络后重试。");
                    throw;
                }
            }
        }

        public async Task<string> DownloadAsync(ReleaseUpdate release, IProgress<int> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateReleaseForDownload(release);
            string directory = null;
            string partialPath = null;
            string finalPath = null;
            bool completed = false;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(TimeSpan.FromMinutes(10));
                try
                {
                    // A unique directory and CreateNew keep retries from executing stale
                    // installers or overwriting another in-progress download.
                    directory = Path.Combine(updatesRoot, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    finalPath = Path.Combine(directory, release.InstallerName);
                    partialPath = finalPath + ".partial";
                    if (progress != null) progress.Report(0);
                    using (HttpResponseMessage response = await GetResponseAsync(release.InstallerUri, false, deadline.Token).ConfigureAwait(false))
                    {
                        long? advertised = response.Content.Headers.ContentLength;
                        if (advertised.HasValue && advertised.Value != release.InstallerSize)
                            throw new UpdateException("安装包大小与发布记录不一致，已停止下载。请稍后重试。");
                        using (deadline.Token.Register(delegate { response.Dispose(); }))
                        using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                        {
                            byte[] buffer = new byte[65536];
                            long total = 0;
                            int lastProgress = -1;
                            while (true)
                            {
                                int read = await input.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false);
                                if (read == 0) break;
                                total += read;
                                if (total > release.InstallerSize || total > MaximumInstallerBytes)
                                    throw new UpdateException("安装包大小超出发布记录，已停止下载。请稍后重试。");
                                await output.WriteAsync(buffer, 0, read, deadline.Token).ConfigureAwait(false);
                                int percent = (int)(total * 99L / release.InstallerSize);
                                if (progress != null && percent != lastProgress) { progress.Report(percent); lastProgress = percent; }
                            }
                            if (total != release.InstallerSize)
                                throw new UpdateException("安装包下载不完整，请检查网络后重试。");
                        }
                    }
                    deadline.Token.ThrowIfCancellationRequested();
                    VerifyInstallerContents(release, partialPath);
                    deadline.Token.ThrowIfCancellationRequested();
                    File.Move(partialPath, finalPath);
                    completed = true;
                    if (progress != null) progress.Report(100);
                    return finalPath;
                }
                catch (UpdateException) { throw; }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new UpdateException("下载安装包超时，请检查网络后重试。");
                }
                catch (Exception ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested) throw new UpdateException("下载安装包超时，请检查网络后重试。");
                    if (ex is PathTooLongException)
                        throw new UpdateException("更新目录路径过长，请前往 GitHub Release 页面下载安装包。");
                    if (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                        throw new UpdateException("无法保存安装包，请检查更新目录的写入权限后重试。");
                    if (IsTransferError(ex)) throw new UpdateException("下载安装包失败，请检查网络和可用磁盘空间后重试。");
                    throw;
                }
                finally
                {
                    if (!completed)
                    {
                        TryDeleteFile(partialPath);
                        TryDeleteFile(finalPath);
                        if (directory != null) try { Directory.Delete(directory, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
            }
        }

        internal static void VerifyInstallerFile(ReleaseUpdate release, string path)
        {
            ValidateReleaseForDownload(release);
            if (String.IsNullOrWhiteSpace(path) || !String.Equals(Path.GetFileName(path), release.InstallerName, StringComparison.Ordinal))
                throw new UpdateException("安装包路径不符合发布记录，请重新下载。");
            try { VerifyInstallerContents(release, path); }
            catch (UpdateException) { throw; }
            catch (IOException) { throw new UpdateException("安装包暂时无法读取，请重新下载。"); }
            catch (UnauthorizedAccessException) { throw new UpdateException("安装包暂时无法读取，请重新下载。"); }
        }

        private static void VerifyInstallerContents(ReleaseUpdate release, string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length != release.InstallerSize || input.ReadByte() != 'M' || input.ReadByte() != 'Z')
                    throw new UpdateException("安装包内容无效，请重新下载。");
                input.Position = 0;
                using (SHA256 hash = SHA256.Create())
                {
                    string actual = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                    if (!String.Equals(actual, release.InstallerDigest, StringComparison.OrdinalIgnoreCase) ||
                        (release.ApiDigest != null && !String.Equals(actual, release.ApiDigest, StringComparison.OrdinalIgnoreCase)))
                        throw new UpdateException("安装包 SHA-256 校验失败，已停止安装。请重新下载。");
                }
            }
        }

        private static void ValidateReleaseForDownload(ReleaseUpdate release)
        {
            if (release == null || release.InstallerSize < 2 || release.InstallerSize > MaximumInstallerBytes ||
                release.InstallerDigest == null || !HashPattern.IsMatch(release.InstallerDigest) ||
                release.InstallerName != InstallerFileName(release.Tag) ||
                !IsReleaseAssetUri(release.InstallerUri, release.Tag, release.InstallerName) ||
                !IsReleaseAssetUri(release.ChecksumsUri, release.Tag, "SHA256SUMS.txt"))
                throw new UpdateException("更新信息不完整，请重新检查更新。");
        }

        private async Task<string> GetTextAsync(Uri uri, int maximumBytes, bool latestApi, CancellationToken token)
        {
            using (HttpResponseMessage response = await GetResponseAsync(uri, latestApi, token).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound && latestApi) return null;
                long? length = response.Content.Headers.ContentLength;
                if (length.HasValue && (length.Value < 0 || length.Value > maximumBytes))
                    throw new UpdateException("GitHub 返回的更新信息过大，请稍后重试。");
                using (token.Register(delegate { response.Dispose(); }))
                using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new MemoryStream())
                {
                    byte[] buffer = new byte[8192];
                    while (true)
                    {
                        int read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                        if (read == 0) break;
                        if (output.Length + read > maximumBytes)
                            throw new UpdateException("GitHub 返回的更新信息过大，请稍后重试。");
                        output.Write(buffer, 0, read);
                    }
                    try { return new UTF8Encoding(false, true).GetString(output.ToArray()).TrimStart('\uFEFF'); }
                    catch (DecoderFallbackException) { throw new UpdateException("GitHub 返回的更新信息无法读取，请稍后重试。"); }
                }
            }
        }

        private async Task<HttpResponseMessage> GetResponseAsync(Uri initialUri, bool latestApi, CancellationToken token)
        {
            Uri uri = initialUri;
            for (int redirects = 0; redirects <= 5; redirects++)
            {
                token.ThrowIfCancellationRequested();
                bool permitted = latestApi ? String.Equals(uri.AbsoluteUri, LatestReleaseUrl, StringComparison.Ordinal) : ValidateDownloadUri(uri);
                if (!latestApi && String.Equals(uri.DnsSafeHost, "github.com", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(Uri.UnescapeDataString(uri.AbsoluteUri), Uri.UnescapeDataString(initialUri.AbsoluteUri), StringComparison.Ordinal)) permitted = false;
                if (!permitted) throw new UpdateException("更新下载地址不受信任，已停止连接。");
                HttpResponseMessage response;
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    if (latestApi)
                    {
                        request.Headers.Accept.ParseAdd("application/vnd.github+json");
                        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                    }
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                }
                int status = (int)response.StatusCode;
                if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                {
                    Uri location = response.Headers.Location;
                    response.Dispose();
                    if (location == null || redirects == 5) throw new UpdateException("GitHub 下载重定向异常，请稍后重试。");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                if (response.IsSuccessStatusCode || (latestApi && response.StatusCode == HttpStatusCode.NotFound)) return response;
                response.Dispose();
                if (status == 403 || status == 429)
                    throw new UpdateException("GitHub 暂时限制了更新请求，请稍后重试，或前往 Release 页面下载。");
                if (status == 404) throw new UpdateException("此版本的安装包暂时不可用，请稍后重试或查看 Release 页面。");
                throw new UpdateException("GitHub 暂时无法提供更新（HTTP " + status + "），请稍后重试。");
            }
            throw new UpdateException("GitHub 下载重定向异常，请稍后重试。");
        }

        internal static bool ValidateDownloadUri(Uri uri)
        {
            if (!IsHttpsUri(uri)) return false;
            string host = uri.DnsSafeHost;
            if (String.Equals(host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(host, "github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return true;
            if (!String.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) || !String.IsNullOrEmpty(uri.Query)) return false;
            string prefix = RepositoryPath + "/releases/download/";
            if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string suffix = uri.AbsolutePath.Substring(prefix.Length);
            string[] parts = suffix.Split('/');
            if (parts.Length != 2) return false;
            string tag = Uri.UnescapeDataString(parts[0]);
            string filename = Uri.UnescapeDataString(parts[1]);
            try
            {
                SemanticVersion version = SemanticVersion.Parse(tag);
                if (!tag.StartsWith("v", StringComparison.Ordinal) || version.PreRelease.Length != 0) return false;
                return filename == "SHA256SUMS.txt" || filename == InstallerFileName(tag);
            }
            catch (UpdateException) { return false; }
        }

        private static bool IsHttpsUri(Uri uri)
        {
            return uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
                uri.Port == 443 && String.IsNullOrEmpty(uri.UserInfo) && String.IsNullOrEmpty(uri.Fragment);
        }

        private static bool IsReleaseAssetUri(Uri uri, string tag, string filename)
        {
            return IsRepositoryUri(uri, RepositoryPath + "/releases/download/" + tag + "/" + filename);
        }

        private static bool IsRepositoryUri(Uri uri, string path)
        {
            if (!IsHttpsUri(uri) || !String.Equals(uri.DnsSafeHost, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !String.IsNullOrEmpty(uri.Query) || uri.AbsolutePath.IndexOf("%2f", StringComparison.OrdinalIgnoreCase) >= 0 ||
                uri.AbsolutePath.IndexOf("%5c", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return String.Equals(Uri.UnescapeDataString(uri.AbsolutePath), path, StringComparison.Ordinal);
        }

        internal static int CompareVersions(string left, string right)
        {
            return SemanticVersion.Parse(left).CompareTo(SemanticVersion.Parse(right));
        }

        internal static ReleaseUpdate ParseLatestRelease(string json, string currentVersion)
        {
            SemanticVersion current = SemanticVersion.Parse(currentVersion);
            if (json == null || Encoding.UTF8.GetByteCount(json) > MaximumMetadataBytes)
                throw new UpdateException("GitHub 返回的更新信息无效，请稍后重试。");
            ReleaseRecord record;
            try
            {
                using (var data = new MemoryStream(Encoding.UTF8.GetBytes(json.TrimStart('\uFEFF'))))
                {
                    var serializer = new DataContractJsonSerializer(typeof(ReleaseRecord), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 16384 });
                    record = (ReleaseRecord)serializer.ReadObject(data);
                }
            }
            catch (Exception ex)
            {
                if (!(ex is SerializationException) && !(ex is InvalidCastException) && !(ex is System.Xml.XmlException) && !(ex is ArgumentException)) throw;
                throw new UpdateException("GitHub 返回的更新信息无法读取，请稍后重试。");
            }
            if (record == null) throw new UpdateException("GitHub 返回的更新信息为空，请稍后重试。");
            if (record.Draft || record.PreRelease) return null;
            SemanticVersion version = SemanticVersion.Parse(record.Tag);
            if (!record.Tag.StartsWith("v", StringComparison.Ordinal)) throw new UpdateException("发布版本格式不受支持，请前往 GitHub Release 查看。");
            if (version.PreRelease.Length != 0 || version.CompareTo(current) <= 0) return null;
            Uri page;
            if (!Uri.TryCreate(record.HtmlUrl, UriKind.Absolute, out page) || !IsRepositoryUri(page, RepositoryPath + "/releases/tag/" + record.Tag))
                throw new UpdateException("更新发布地址不受信任，请前往 GitHub 仓库查看。");
            if (record.Assets == null) throw new UpdateException("此版本尚未提供 Windows 安装包，请稍后重试。");
            string installerName = InstallerFileName(record.Tag);
            AssetRecord installer = null;
            AssetRecord checksums = null;
            foreach (AssetRecord asset in record.Assets)
            {
                if (asset == null) continue;
                if (asset.Name == installerName)
                {
                    if (installer != null) throw new UpdateException("发布安装包信息重复，请前往 GitHub Release 查看。");
                    installer = asset;
                }
                if (asset.Name == "SHA256SUMS.txt")
                {
                    if (checksums != null) throw new UpdateException("发布校验信息重复，请前往 GitHub Release 查看。");
                    checksums = asset;
                }
            }
            Uri installerUri, checksumsUri;
            if (installer == null || checksums == null || installer.State != "uploaded" || checksums.State != "uploaded" ||
                installer.Size < 2 || installer.Size > MaximumInstallerBytes || checksums.Size < 1 || checksums.Size > MaximumChecksumsBytes ||
                !Uri.TryCreate(installer.DownloadUrl, UriKind.Absolute, out installerUri) || !IsReleaseAssetUri(installerUri, record.Tag, installerName) ||
                !Uri.TryCreate(checksums.DownloadUrl, UriKind.Absolute, out checksumsUri) || !IsReleaseAssetUri(checksumsUri, record.Tag, "SHA256SUMS.txt"))
                throw new UpdateException("此版本的安装包或校验信息不完整，请稍后重试或查看 Release 页面。");
            string apiDigest = null;
            if (!String.IsNullOrEmpty(installer.Digest))
            {
                if (!installer.Digest.StartsWith("sha256:", StringComparison.Ordinal) || !HashPattern.IsMatch(installer.Digest.Substring(7)))
                    throw new UpdateException("发布安装包的校验格式不受支持，请前往 GitHub Release 查看。");
                apiDigest = installer.Digest.Substring(7).ToLowerInvariant();
            }
            return new ReleaseUpdate(record.Tag, page, installerName, installer.Size, installerUri, checksumsUri, apiDigest);
        }

        internal static string ParseInstallerChecksum(string text, string installerName)
        {
            if (text == null || Encoding.UTF8.GetByteCount(text) > MaximumChecksumsBytes)
                throw new UpdateException("发布校验文件无效，请稍后重试。");
            string result = null;
            foreach (string raw in text.TrimStart('\uFEFF').Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (String.IsNullOrWhiteSpace(line)) continue;
                Match match = ChecksumLinePattern.Match(line);
                if (!match.Success) throw new UpdateException("发布校验文件格式无效，请稍后重试。");
                if (match.Groups[2].Value != installerName) continue;
                if (result != null) throw new UpdateException("发布校验文件包含重复安装包，请前往 GitHub Release 查看。");
                result = match.Groups[1].Value.ToLowerInvariant();
            }
            if (result == null) throw new UpdateException("发布校验文件缺少此安装包，请前往 GitHub Release 查看。");
            return result;
        }

        private static string InstallerFileName(string tag) { return "Screenline-" + tag + "-windows-x64-setup.exe"; }

        private static bool IsTransferError(Exception ex)
        {
            return ex is HttpRequestException || ex is WebException || ex is IOException || ex is ObjectDisposedException;
        }

        private static void TryDeleteFile(string path)
        {
            if (path == null) return;
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        public void Dispose() { client.Dispose(); }

        [DataContract]
        private sealed class ReleaseRecord
        {
            [DataMember(Name = "tag_name", IsRequired = true)] public string Tag { get; set; }
            [DataMember(Name = "html_url", IsRequired = true)] public string HtmlUrl { get; set; }
            [DataMember(Name = "draft", IsRequired = true)] public bool Draft { get; set; }
            [DataMember(Name = "prerelease", IsRequired = true)] public bool PreRelease { get; set; }
            [DataMember(Name = "assets")] public List<AssetRecord> Assets { get; set; }
        }

        [DataContract]
        private sealed class AssetRecord
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "state")] public string State { get; set; }
            [DataMember(Name = "size")] public long Size { get; set; }
            [DataMember(Name = "browser_download_url")] public string DownloadUrl { get; set; }
            [DataMember(Name = "digest")] public string Digest { get; set; }
        }

        private sealed class SemanticVersion : IComparable<SemanticVersion>
        {
            private static readonly Regex Pattern = new Regex(@"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z", RegexOptions.CultureInvariant);
            private readonly string[] core;
            internal readonly string[] PreRelease;

            private SemanticVersion(string[] core, string[] preRelease) { this.core = core; PreRelease = preRelease; }

            internal static SemanticVersion Parse(string value)
            {
                if (String.IsNullOrEmpty(value) || value.Length > 256) throw new UpdateException("版本号格式无法识别，请前往 GitHub Release 查看。");
                Match match = Pattern.Match(value);
                if (!match.Success) throw new UpdateException("版本号格式无法识别，请前往 GitHub Release 查看。");
                string[] prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : new string[0];
                foreach (string identifier in prerelease)
                    if (IsNumeric(identifier) && identifier.Length > 1 && identifier[0] == '0')
                        throw new UpdateException("版本号格式无法识别，请前往 GitHub Release 查看。");
                return new SemanticVersion(new string[] { match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value }, prerelease);
            }

            public int CompareTo(SemanticVersion other)
            {
                for (int i = 0; i < 3; i++)
                {
                    int comparison = CompareNumeric(core[i], other.core[i]);
                    if (comparison != 0) return comparison;
                }
                if (PreRelease.Length == 0 || other.PreRelease.Length == 0)
                    return PreRelease.Length == other.PreRelease.Length ? 0 : (PreRelease.Length == 0 ? 1 : -1);
                for (int i = 0; i < Math.Min(PreRelease.Length, other.PreRelease.Length); i++)
                {
                    bool leftNumber = IsNumeric(PreRelease[i]);
                    bool rightNumber = IsNumeric(other.PreRelease[i]);
                    int comparison = leftNumber && rightNumber ? CompareNumeric(PreRelease[i], other.PreRelease[i]) :
                        (leftNumber != rightNumber ? (leftNumber ? -1 : 1) : String.CompareOrdinal(PreRelease[i], other.PreRelease[i]));
                    if (comparison != 0) return comparison;
                }
                return PreRelease.Length.CompareTo(other.PreRelease.Length);
            }

            private static int CompareNumeric(string left, string right)
            {
                return left.Length == right.Length ? String.CompareOrdinal(left, right) : left.Length.CompareTo(right.Length);
            }

            private static bool IsNumeric(string value)
            {
                foreach (char character in value) if (character < '0' || character > '9') return false;
                return true;
            }
        }
    }
}
