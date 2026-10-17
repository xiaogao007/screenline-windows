using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Screenline
{
    // All updater fixtures stay in the self-test sandbox. The handler never opens a socket,
    // and the synthetic MZ bytes are verified as data without executing an installer.
    internal static class UpdateVerification
    {
        private const string Tag = "v0.1.4";
        private const string InstallerName = "Screenline-v0.1.4-windows-x64-setup.exe";
        private const string ReleaseUrl = "https://github.com/xiaogao007/screenline-windows/releases/tag/v0.1.4";
        private const string AssetBase = "https://github.com/xiaogao007/screenline-windows/releases/download/v0.1.4/";
        private static readonly byte[] installerBytes = MakeInstallerBytes();
        private static readonly string installerHash = Hash(installerBytes);
        private static int fixtureId;

        internal static void Run(string sandbox, Action<bool, string> check)
        {
            // Keep a failed async operation from blocking the WPF dispatcher used by other tests.
            Task.Run(delegate { RunServiceTests(Path.Combine(sandbox, "u"), check); }).GetAwaiter().GetResult();
            SynchronizationContext previous = SynchronizationContext.Current;
            try
            {
                // The self-test shows windows without Application.Run. Install the same
                // dispatcher context as the real WPF message loop for await/progress.
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                RunWindowTests(Path.Combine(sandbox, "w"), Path.GetDirectoryName(sandbox), check);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        private static void RunServiceTests(string sandbox, Action<bool, string> check)
        {
            Directory.CreateDirectory(sandbox);
            check(UpdateService.CompareVersions("v1.2.3", "1.2.3+build.9") == 0 &&
                UpdateService.CompareVersions("1.2.4", "1.2.3") > 0 &&
                UpdateService.CompareVersions("1.1.99", "1.2.0") < 0,
                "Updater compares equal, newer, and older semantic versions without treating build metadata as an update");
            check(UpdateService.CompareVersions("1.2.3-rc.2", "1.2.3-rc.10") < 0 &&
                UpdateService.CompareVersions("1.2.3-rc.10", "1.2.3") < 0 &&
                UpdateService.CompareVersions("1.2.3-alpha.1", "1.2.3-alpha.beta") < 0,
                "Updater follows numeric prerelease ordering and prefers the stable release");
            check(UpdateService.CompareVersions("999999999999999999999999.2.3", "999999999999999999999998.9.9") > 0 &&
                UpdateService.CompareVersions("1.2.3-rc.999999999999999999999999", "1.2.3-rc.999999999999999999999998") > 0,
                "Updater compares large semantic-version numbers without integer overflow");
            check(Rejected(delegate { UpdateService.CompareVersions("1.02.3", "1.2.3"); }) &&
                Rejected(delegate { UpdateService.CompareVersions("1.2", "1.2.3"); }) &&
                Rejected(delegate { UpdateService.CompareVersions("1.2.3-01", "1.2.3"); }),
                "Updater rejects malformed semantic versions and numeric identifiers with leading zeroes");

            string valid = ReleaseJson(false, false, true, true, AssetBase, installerHash, installerBytes.Length);
            using (var fixture = new Fixture(sandbox, valid))
            {
                ReleaseUpdate update = fixture.Check("0.1.3");
                check(update != null && update.Tag == Tag && update.InstallerName == InstallerName &&
                    update.InstallerSize == installerBytes.Length && update.ReleaseUri.AbsoluteUri == ReleaseUrl &&
                    String.Equals(update.InstallerDigest, installerHash, StringComparison.OrdinalIgnoreCase),
                    "Updater resolves the stable release and its exact Windows installer with a verified checksum");
                check(fixture.Handler.Requests.Count == 2 && fixture.Handler.Requests[0].AbsoluteUri ==
                    "https://api.github.com/repos/xiaogao007/screenline-windows/releases/latest" &&
                    fixture.Handler.Requests[1].AbsoluteUri == AssetBase + "SHA256SUMS.txt",
                    "Updater checks the fixed repository latest-release endpoint and checksum asset through the injected transport");
            }
            using (var fixture = new Fixture(sandbox, valid))
                check(fixture.Check("0.1.4") == null && fixture.Check("0.1.5") == null,
                    "An equal or older GitHub release does not offer an update");
            using (var fixture = new Fixture(sandbox, valid))
                check(fixture.Check("0.1.4-rc.1") != null,
                    "A current prerelease can update to the matching stable version");
            using (var draft = new Fixture(sandbox, ReleaseJson(true, false, true, true, AssetBase, installerHash, installerBytes.Length)))
            using (var prerelease = new Fixture(sandbox, ReleaseJson(false, true, true, true, AssetBase, installerHash, installerBytes.Length)))
                check(draft.Check("0.1.3") == null && prerelease.Check("0.1.3") == null,
                    "Draft and prerelease GitHub releases are excluded from stable update offers");
            using (var noInstaller = new Fixture(sandbox, ReleaseJson(false, false, false, true, AssetBase, installerHash, installerBytes.Length)))
            using (var noChecksum = new Fixture(sandbox, ReleaseJson(false, false, true, false, AssetBase, installerHash, installerBytes.Length)))
                check(Rejected(delegate { noInstaller.Check("0.1.3"); }) && Rejected(delegate { noChecksum.Check("0.1.3"); }),
                    "A release without its exact Windows installer or checksum asset cannot be offered");
            using (var foreign = new Fixture(sandbox, ReleaseJson(false, false, true, true, "https://example.com/", installerHash, installerBytes.Length)))
                check(Rejected(delegate { foreign.Check("0.1.3"); }) && foreign.Handler.Requests.Count == 1,
                    "A release containing foreign asset URLs is rejected before requesting those assets");
            using (var foreignPage = new Fixture(sandbox, valid.Replace(ReleaseUrl, "https://github.com/other/project/releases/tag/v0.1.4")))
                check(Rejected(delegate { foreignPage.Check("0.1.3"); }),
                    "Update release-page links must belong to the configured Screenline repository");
            check(!UpdateService.ValidateDownloadUri(new Uri("http://github.com/xiaogao007/screenline-windows/releases/download/v0.1.4/" + InstallerName)) &&
                !UpdateService.ValidateDownloadUri(new Uri("https://github.com.evil.example/xiaogao007/screenline-windows/releases/download/v0.1.4/" + InstallerName)) &&
                !UpdateService.ValidateDownloadUri(new Uri("https://user@github.com/xiaogao007/screenline-windows/releases/download/v0.1.4/" + InstallerName)) &&
                !UpdateService.ValidateDownloadUri(new Uri("https://github.com:444/xiaogao007/screenline-windows/releases/download/v0.1.4/" + InstallerName)),
                "Download URL validation excludes plain HTTP, lookalike hosts, embedded credentials, and unexpected ports");
            using (var malformed = new Fixture(sandbox, "{ invalid json"))
                check(Rejected(delegate { malformed.Check("0.1.3"); }),
                    "Malformed release JSON produces a controlled updater error");
            using (var limited = new Fixture(sandbox, valid))
            {
                limited.Handler.Override = delegate(HttpRequestMessage request, CancellationToken token)
                { return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("rate limit exceeded") }; };
                check(Rejected(delegate { limited.Check("0.1.3"); }),
                    "GitHub rate-limit or authorization failure produces a controlled updater error");
            }
            using (var unavailable = new Fixture(sandbox, valid))
            {
                unavailable.Handler.Override = delegate(HttpRequestMessage request, CancellationToken token)
                { throw new HttpRequestException("Synthetic offline failure"); };
                check(Rejected(delegate { unavailable.Check("0.1.3"); }),
                    "An offline connection failure produces a controlled updater error");
            }
            using (var emptyRepository = new Fixture(sandbox, valid))
            {
                emptyRepository.Handler.Override = delegate(HttpRequestMessage request, CancellationToken token)
                { return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("No stable release") }; };
                check(emptyRepository.Check("0.1.3") == null,
                    "A repository without a public stable release does not offer an update");
            }
            using (var oversized = new Fixture(sandbox, valid))
            {
                oversized.Handler.Override = delegate(HttpRequestMessage request, CancellationToken token)
                { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1024 * 1024 + 1)) }; };
                check(Rejected(delegate { oversized.Check("0.1.3"); }),
                    "Release metadata over the bounded response limit is rejected before parsing");
            }
            using (var canceled = new Fixture(sandbox, valid))
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                check(Canceled(delegate { canceled.Service.CheckAsync("0.1.3", cancellation.Token).GetAwaiter().GetResult(); }) &&
                    canceled.Handler.Requests.Count == 0,
                    "An already-canceled update check makes no network request");
            }

            check(UpdateService.ParseInstallerChecksum(new string('0', 64) + "  other.zip\n" + installerHash + "  " + InstallerName + "\n", InstallerName) == installerHash &&
                Rejected(delegate { UpdateService.ParseInstallerChecksum(new string('0', 64) + "  other.zip\n", InstallerName); }) &&
                Rejected(delegate { UpdateService.ParseInstallerChecksum(installerHash + "  " + InstallerName + "\n" + new string('0', 64) + "  " + InstallerName, InstallerName); }),
                "Checksum parsing selects the exact installer and rejects missing or conflicting entries");
            using (var inconsistent = new Fixture(sandbox, ReleaseJson(false, false, true, true, AssetBase, new string('0', 64), installerBytes.Length)))
                check(Rejected(delegate { inconsistent.Check("0.1.3"); }),
                    "A GitHub asset digest that disagrees with SHA256SUMS blocks the update");

            VerifyDownloads(sandbox, valid, check);
        }

        private static void VerifyDownloads(string sandbox, string valid, Action<bool, string> check)
        {
            using (var fixture = new Fixture(sandbox, valid))
            {
                ReleaseUpdate update = fixture.Check("0.1.3");
                var progress = new RecordedProgress();
                string path = fixture.Service.DownloadAsync(update, progress, CancellationToken.None).GetAwaiter().GetResult();
                check(File.Exists(path) && String.Equals(Path.GetFileName(path), InstallerName, StringComparison.Ordinal) &&
                    Path.GetFullPath(path).StartsWith(Path.GetFullPath(fixture.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    File.ReadAllBytes(path).SequenceEqual(installerBytes) && progress.Values.Count > 0 &&
                    progress.Values.All(delegate(int value) { return value >= 0 && value <= 100; }) && progress.Values.Last() == 100,
                    "A successful update download atomically yields the exact verified installer inside its isolated cache and completes progress");
            }
            using (var corrupt = new Fixture(sandbox, valid))
            {
                ReleaseUpdate update = corrupt.Check("0.1.3");
                byte[] altered = (byte[])installerBytes.Clone(); altered[altered.Length - 1] ^= 1;
                corrupt.Handler.DownloadBytes = altered;
                var progress = new RecordedProgress();
                check(Rejected(delegate { corrupt.Service.DownloadAsync(update, progress, CancellationToken.None).GetAwaiter().GetResult(); }) &&
                    !progress.Values.Contains(100) &&
                    !Directory.GetFiles(corrupt.Root, "*.exe", SearchOption.AllDirectories).Any(),
                    "A downloaded installer with the wrong SHA-256 never completes verification progress and leaves no runnable installer");
            }
            using (var truncated = new Fixture(sandbox, valid))
            {
                ReleaseUpdate update = truncated.Check("0.1.3");
                truncated.Handler.DownloadBytes = installerBytes.Take(installerBytes.Length / 2).ToArray();
                check(Rejected(delegate { truncated.Service.DownloadAsync(update, null, CancellationToken.None).GetAwaiter().GetResult(); }) &&
                    !Directory.GetFiles(truncated.Root, "*.exe", SearchOption.AllDirectories).Any(),
                    "A truncated installer is rejected using the advertised asset size and leaves no runnable installer");
            }
            byte[] notExecutable = Encoding.ASCII.GetBytes("Synthetic non-PE payload: never execute this text.");
            using (var notMZ = new Fixture(sandbox, ReleaseJson(false, false, true, true, AssetBase, Hash(notExecutable), notExecutable.Length)))
            {
                notMZ.Handler.DownloadBytes = notExecutable;
                notMZ.Handler.ChecksumText = Hash(notExecutable) + "  " + InstallerName + "\n";
                ReleaseUpdate update = notMZ.Check("0.1.3");
                check(Rejected(delegate { notMZ.Service.DownloadAsync(update, null, CancellationToken.None).GetAwaiter().GetResult(); }) &&
                    !Directory.GetFiles(notMZ.Root, "*.exe", SearchOption.AllDirectories).Any(),
                    "Matching size and checksum do not allow a payload without the Windows executable signature");
            }
            using (var interrupted = new Fixture(sandbox, valid))
            using (var cancellation = new CancellationTokenSource())
            {
                ReleaseUpdate update = interrupted.Check("0.1.3");
                interrupted.Handler.DownloadStream = delegate { return new CancelingStream(installerBytes, cancellation); };
                check(Canceled(delegate { interrupted.Service.DownloadAsync(update, null, cancellation.Token).GetAwaiter().GetResult(); }) &&
                    !Directory.GetFiles(interrupted.Root, "*.exe", SearchOption.AllDirectories).Any() &&
                    !Directory.GetFiles(interrupted.Root, "*.partial", SearchOption.AllDirectories).Any(),
                    "Canceling an installer download removes its partial file and yields no runnable installer");
            }
            using (var redirected = new Fixture(sandbox, valid))
            {
                ReleaseUpdate update = redirected.Check("0.1.3");
                redirected.Handler.Override = delegate(HttpRequestMessage request, CancellationToken token)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                    response.Headers.Location = new Uri("https://example.com/" + InstallerName);
                    return response;
                };
                check(Rejected(delegate { redirected.Service.DownloadAsync(update, null, CancellationToken.None).GetAwaiter().GetResult(); }) &&
                    redirected.Handler.Requests.Count == 3 && !Directory.GetFiles(redirected.Root, "*.exe", SearchOption.AllDirectories).Any(),
                    "A redirect to an untrusted download host is blocked before that host is requested");
            }
            using (var redirected = new Fixture(sandbox, valid))
            {
                ReleaseUpdate update = redirected.Check("0.1.3");
                redirected.Handler.Override = delegate(HttpRequestMessage request, CancellationToken token)
                {
                    if (request.RequestUri.Host == "github.com")
                    {
                        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                        response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/synthetic/" + InstallerName + "?signed=fixture");
                        return response;
                    }
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installerBytes) };
                };
                string path = redirected.Service.DownloadAsync(update, null, CancellationToken.None).GetAwaiter().GetResult();
                check(File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(installerBytes) && redirected.Handler.Requests.Count == 4,
                    "A normal GitHub release-asset CDN redirect is followed and its installer still passes size and SHA-256 verification");
            }
        }

        private static bool Rejected(Action operation)
        {
            try { operation(); return false; }
            catch (UpdateException) { return true; }
        }

        private static bool Canceled(Action operation)
        {
            try { operation(); return false; }
            catch (OperationCanceledException) { return true; }
        }

        private static string ReleaseJson(bool draft, bool prerelease, bool includeInstaller, bool includeChecksum, string assetBase, string apiHash, int length)
        {
            var assets = new List<string>();
            if (includeInstaller) assets.Add("{\"name\":\"" + InstallerName + "\",\"state\":\"uploaded\",\"size\":" + length +
                ",\"digest\":\"sha256:" + apiHash + "\",\"browser_download_url\":\"" + assetBase + InstallerName + "\"}");
            if (includeChecksum) assets.Add("{\"name\":\"SHA256SUMS.txt\",\"state\":\"uploaded\",\"size\":" + Encoding.UTF8.GetByteCount(installerHash + "  " + InstallerName + "\n") + ",\"browser_download_url\":\"" + assetBase + "SHA256SUMS.txt\"}");
            return "{\"tag_name\":\"" + Tag + "\",\"html_url\":\"" + ReleaseUrl + "\",\"draft\":" + draft.ToString().ToLowerInvariant() +
                ",\"prerelease\":" + prerelease.ToString().ToLowerInvariant() + ",\"assets\":[" + String.Join(",", assets) + "]}";
        }

        private static byte[] MakeInstallerBytes()
        {
            byte[] bytes = new byte[128]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            for (int i = 2; i < bytes.Length; i++) bytes[i] = (byte)(i * 17);
            return bytes;
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static void RunWindowTests(string sandbox, string destination, Action<bool, string> check)
        {
            Directory.CreateDirectory(sandbox);
            string valid = ReleaseJson(false, false, true, true, AssetBase, installerHash, installerBytes.Length);
            using (var fixture = new Fixture(sandbox, valid))
            {
                ReleaseUpdate knownUpdate = fixture.Check("0.1.3");
                int requests = fixture.Handler.Requests.Count;
                int launches = 0;
                var window = new UpdateWindow(fixture.Service, delegate { launches++; }, "0.1.3", false);
                try {
                    window.PresentUpdate(knownUpdate); window.Show(); window.UpdateLayout();
                    check(window.State == "Available" && FindButton(window, "下载 Windows 安装包") != null &&
                        fixture.Handler.Requests.Count == requests && launches == 0 && !Directory.GetFiles(fixture.Root, "*.exe", SearchOption.AllDirectories).Any(),
                        "A startup update reminder opens the known release without rechecking, downloading, or starting an installer");
                }
                finally { window.Close(); }
            }
            using (var fixture = new Fixture(sandbox, valid))
            {
                int launches = 0; string launchedPath = null;
                var window = new UpdateWindow(fixture.Service, delegate(string path) { launches++; launchedPath = path; }, "0.1.3", false);
                try
                {
                    window.Show(); window.UpdateLayout();
                    check(window.State == "Idle" && fixture.Handler.Requests.Count == 0 && FindButton(window, "检查更新") != null &&
                        FindButton(window, "查看版本说明") != null && FindButton(window, "关闭") != null,
                        "The updater window starts idle with named check, release notes, and close actions without a background request");
                    Pump(window.CheckForUpdatesAsync());
                    check(window.State == "Available" && FindButton(window, "下载 Windows 安装包") != null && launches == 0,
                        "A newer release presents the installer download action without starting a download or process");
                    const string localNotes = "国内更新说明\n保留截图与设置。\n<文字按纯文本显示，不运行脚本>";
                    var notesWindow = window.CreateReleaseNotesWindow("0.1.7", localNotes);
                    notesWindow.Show(); notesWindow.UpdateLayout();
                    var notesText = Descendants<TextBox>(notesWindow).Single();
                    check(notesWindow.Owner == window && notesText.IsReadOnly && notesText.Text == localNotes &&
                        AutomationProperties.GetName(notesText) == "版本说明正文" && FindButton(notesWindow, "关闭版本说明") != null,
                        "Domestic release notes are readable and copyable as local plain text without opening GitHub or executing markup");
                    Render(notesWindow, Path.Combine(destination, "update-notes-preview.png")); notesWindow.Close();
                    check(Descendants<TextBlock>(window).Any(delegate(TextBlock text) {
                        return AutomationProperties.GetName(text) == "更新状态" &&
                            AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite;
                    }) && Descendants<ProgressBar>(window).Any(delegate(ProgressBar bar) { return AutomationProperties.GetName(bar) == "更新下载进度"; }),
                        "Updater status is a polite live region and download progress has an accessible name");
                    window.Width = 500; window.Height = 370; window.UpdateLayout();
                    Render(window, Path.Combine(destination, "update-preview.png"));
                    check(FindButton(window, "下载 Windows 安装包").IsVisible && FindButton(window, "关闭").IsVisible &&
                        Descendants<ScrollViewer>(window).Any(),
                        "Updater retains visible actions and a scrollable body at its supported minimum window size");
                    Pump(window.DownloadInstallerAsync());
                    check(window.State == "Ready" && FindButton(window, "退出并安装") != null && launches == 0,
                        "A verified completed download waits for the explicit exit-and-install action");
                    window.RunInstaller();
                    check(launches == 1 && File.Exists(launchedPath) && File.ReadAllBytes(launchedPath).SequenceEqual(installerBytes),
                        "Only the explicit install action hands the reverified installer to the injected launcher without executing a process in self-tests");
                }
                finally { window.Close(); }
            }
            using (var fixture = new Fixture(sandbox, valid))
            {
                int launches = 0;
                var window = new UpdateWindow(fixture.Service, delegate { launches++; }, "0.1.3", false);
                try
                {
                    window.Show(); Pump(window.CheckForUpdatesAsync()); Pump(window.DownloadInstallerAsync());
                    string downloaded = Directory.GetFiles(fixture.Root, "*.exe", SearchOption.AllDirectories).Single();
                    byte[] changed = File.ReadAllBytes(downloaded); changed[changed.Length - 1] ^= 1; File.WriteAllBytes(downloaded, changed);
                    window.RunInstaller();
                    check(launches == 0 && window.State == "Available" && FindButton(window, "下载 Windows 安装包").IsEnabled,
                        "The updater rehashes immediately before launch and offers a retry if the cached installer was changed");
                }
                finally { window.Close(); }
            }
            using (var fixture = new Fixture(sandbox, valid))
            {
                var window = new UpdateWindow(fixture.Service, delegate { throw new InvalidOperationException("No launch expected"); }, "0.1.4", false);
                try
                {
                    window.Show(); Pump(window.CheckForUpdatesAsync());
                    check(window.State == "Current" && FindButton(window, "重新检查").IsEnabled &&
                        Descendants<TextBlock>(window).Any(delegate(TextBlock text) { return text.Text == "当前已是最新版本"; }),
                        "An equal release renders the current-version state with a working retry action");
                }
                finally { window.Close(); }
            }
            using (var fixture = new Fixture(sandbox, "{ invalid json"))
            {
                var window = new UpdateWindow(fixture.Service, delegate { throw new InvalidOperationException("No launch expected"); }, "0.1.3", false);
                try
                {
                    window.Show(); Pump(window.CheckForUpdatesAsync());
                    check(window.State == "Error" && FindButton(window, "重新检查").IsEnabled && FindButton(window, "查看版本说明").IsEnabled,
                        "A failed update check renders a recoverable error with retry and manual release access");
                }
                finally { window.Close(); }
            }
            using (var fixture = new Fixture(sandbox, valid))
            {
                var window = new UpdateWindow(fixture.Service, delegate { throw new InvalidOperationException("No launch expected"); }, "0.1.3", false);
                try
                {
                    window.Show();
                    fixture.Handler.AsyncOverride = WaitForCancellation;
                    Task checking = window.CheckForUpdatesAsync();
                    check(window.State == "Checking" && FindButton(window, "取消检查").IsEnabled,
                        "An in-progress update check exposes an enabled cancel action");
                    FindButton(window, "取消检查").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(checking);
                    check(window.State == "Idle" && FindButton(window, "检查更新").IsEnabled,
                        "Canceling an update check restores the idle state and permits a retry");
                    fixture.Handler.AsyncOverride = null;
                    Pump(window.CheckForUpdatesAsync());
                    fixture.Handler.AsyncOverride = WaitForCancellation;
                    Task downloading = window.DownloadInstallerAsync();
                    check(window.State == "Downloading" && FindButton(window, "取消下载").IsEnabled,
                        "An in-progress installer download exposes an enabled cancel action");
                    FindButton(window, "取消下载").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(downloading);
                    check(window.State == "Available" && FindButton(window, "下载 Windows 安装包").IsEnabled &&
                        !Directory.GetFiles(fixture.Root, "*.exe", SearchOption.AllDirectories).Any(),
                        "Canceling a download returns to the available release without leaving or launching an installer");
                }
                finally { window.Close(); }
            }
        }

        private static Task<HttpResponseMessage> WaitForCancellation(HttpRequestMessage request, CancellationToken token)
        {
            var completion = new TaskCompletionSource<HttpResponseMessage>();
            token.Register(delegate { completion.TrySetCanceled(); });
            return completion.Task;
        }

        private static void Pump(Task operation)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (!operation.IsCompleted)
            {
                if (elapsed.ElapsedMilliseconds > 10000) throw new TimeoutException("Offline updater UI operation exceeded the self-test timeout.");
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
                timer.Start(); Dispatcher.PushFrame(frame);
            }
            operation.GetAwaiter().GetResult();
        }

        private static Button FindButton(DependencyObject root, string name)
        {
            return Descendants<Button>(root).FirstOrDefault(delegate(Button button) { return AutomationProperties.GetName(button) == name; });
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T) yield return (T)child;
                foreach (T nested in Descendants<T>(child)) yield return nested;
            }
        }

        private static void Render(Window window, string path)
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(path)) encoder.Save(stream);
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly string Root;
            internal readonly FakeHandler Handler;
            internal readonly UpdateService Service;
            internal Fixture(string sandbox, string json)
            {
                // The outer sandbox already has a unique ID. Short fixture components
                // keep legacy .NET path handling working even in a long checkout path.
                Root = Path.Combine(sandbox, Interlocked.Increment(ref fixtureId).ToString("x"));
                Directory.CreateDirectory(Root);
                Handler = new FakeHandler(json);
                Service = new UpdateService(Handler, Root);
            }
            internal ReleaseUpdate Check(string current) { return Service.CheckAsync(current, CancellationToken.None).GetAwaiter().GetResult(); }
            public void Dispose() { Service.Dispose(); }
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly string json;
            internal readonly List<Uri> Requests = new List<Uri>();
            internal byte[] DownloadBytes = installerBytes;
            internal string ChecksumText = installerHash + "  " + InstallerName + "\n";
            internal Func<Stream> DownloadStream;
            internal Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> Override;
            internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> AsyncOverride;
            internal FakeHandler(string responseJson) { json = responseJson; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Requests.Add(request.RequestUri);
                if (AsyncOverride != null) return AsyncOverride(request, cancellationToken);
                if (Override != null) return Task.FromResult(Override(request, cancellationToken));
                HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);
                if (request.RequestUri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal)) response.Content = new StringContent(json, Encoding.UTF8, "application/json");
                else if (request.RequestUri.AbsolutePath.EndsWith("/SHA256SUMS.txt", StringComparison.Ordinal)) response.Content = new StringContent(ChecksumText, Encoding.UTF8, "text/plain");
                else if (request.RequestUri.AbsolutePath.EndsWith("/" + InstallerName, StringComparison.Ordinal))
                    response.Content = DownloadStream == null ? (HttpContent)new ByteArrayContent(DownloadBytes) : new StreamContent(DownloadStream());
                else throw new InvalidOperationException("Unexpected updater request in offline self-test: " + request.RequestUri.AbsoluteUri);
                return Task.FromResult(response);
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
