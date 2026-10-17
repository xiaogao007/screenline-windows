using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Screenline
{
    internal sealed class UpdateWindow : Window
    {
        private readonly UpdateService service;
        private readonly Action<string> launchInstaller;
        private readonly string currentVersion;
        private readonly TextBlock heading, details, status;
        private readonly Button primary, releasePage, close;
        private readonly ProgressBar progress;
        private CancellationTokenSource operation;
        private ReleaseUpdate update;
        private string installerPath;
        private bool closed;
        internal string State { get; private set; }

        internal UpdateWindow(Action<string> launchInstaller)
            : this(new UpdateService(), launchInstaller, AppVersion.Current, true) { }

        internal UpdateWindow(Action<string> launchInstaller, ReleaseUpdate initialUpdate)
            : this(new UpdateService(), launchInstaller, AppVersion.Current, initialUpdate == null)
        {
            if (initialUpdate != null) PresentUpdate(initialUpdate);
        }

        internal UpdateWindow(UpdateService service, Action<string> launchInstaller, string currentVersion, bool autoCheck)
        {
            this.service = service;
            this.launchInstaller = launchInstaller;
            this.currentVersion = currentVersion;
            Title = "截图晾台 · 检查更新";
            Width = 590; Height = 430; MinWidth = 500; MinHeight = 370;
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Theme.Surface; Foreground = Theme.Foreground; FontFamily = Theme.Font;
            var outer = new DockPanel { Margin = new Thickness(28) };
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            DockPanel.SetDock(actions, Dock.Bottom); outer.Children.Add(actions);
            releasePage = Theme.Button("查看版本说明", false); releasePage.Width = 120;
            releasePage.Margin = new Thickness(0, 4, 8, 0);
            releasePage.Click += OpenReleasePage; actions.Children.Add(releasePage);
            primary = Theme.Button("检查更新", true); primary.Width = 164;
            primary.Margin = new Thickness(0, 4, 8, 0); primary.IsDefault = true;
            primary.Click += PrimaryClicked; actions.Children.Add(primary);
            close = Theme.Button("关闭", false); close.Width = 100; close.Margin = new Thickness(0, 4, 0, 0);
            close.Click += delegate { if (operation != null) operation.Cancel(); else Close(); }; actions.Children.Add(close);
            var body = new StackPanel();
            outer.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            body.Children.Add(new TextBlock { Text = "截图晾台 Screenline", FontSize = 24, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(Note("当前版本 " + currentVersion));
            heading = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 24, 0, 8) };
            body.Children.Add(heading);
            details = Note(""); body.Children.Add(details);
            progress = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 16, 0, 8), Visibility = Visibility.Collapsed };
            AutomationProperties.SetName(progress, "更新下载进度"); body.Children.Add(progress);
            status = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 48, Margin = new Thickness(0, 12, 0, 0), Foreground = Theme.Foreground };
            AutomationProperties.SetName(status, "更新状态"); AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            body.Children.Add(status);
            Content = outer;
            SetState("Idle", "检查 GitHub 最新正式版本", "点击检查更新后连接 GitHub。下载完成后，可选择退出应用并打开安装向导。", "");
            Loaded += async delegate { if (autoCheck && State == "Idle") await CheckForUpdatesAsync(); close.Focus(); };
            Closed += delegate {
                closed = true;
                if (operation != null) operation.Cancel();
                service.Dispose();
            };
            PreviewKeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs e) {
                if (e.Key == System.Windows.Input.Key.Escape) {
                    if (operation != null) operation.Cancel(); else Close();
                    e.Handled = true;
                }
            };
        }

        private static TextBlock Note(string text)
        {
            return new TextBlock { Text = text, FontSize = 12, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        }

        private void SetState(string value, string title, string description, string message)
        {
            if (closed) return;
            State = value; heading.Text = title; details.Text = description; status.Text = message;
            bool working = value == "Checking" || value == "Downloading";
            string label = value == "Available" ? "下载 Windows 安装包" : value == "Ready" ? "退出并安装" : value == "Downloading" ? "正在下载…" : value == "Checking" ? "正在检查…" : "重新检查";
            if (value == "Idle") label = "检查更新";
            primary.Content = label; AutomationProperties.SetName(primary, label); primary.IsEnabled = !working;
            string closeLabel = value == "Downloading" ? "取消下载" : value == "Checking" ? "取消检查" : "关闭";
            close.Content = closeLabel; AutomationProperties.SetName(close, closeLabel);
            releasePage.IsEnabled = !working;
            progress.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
            progress.IsIndeterminate = value == "Checking";
            if (value != "Downloading") progress.Value = 0;
        }

        private async void PrimaryClicked(object sender, RoutedEventArgs e)
        {
            if (State == "Available") await DownloadInstallerAsync();
            else if (State == "Ready") RunInstaller();
            else await CheckForUpdatesAsync();
        }

        internal async Task CheckForUpdatesAsync()
        {
            if (closed || operation != null) return;
            var request = new CancellationTokenSource(); operation = request;
            update = null; installerPath = null;
            SetState("Checking", "正在检查更新…", "正在从 GitHub Release 获取最新正式版本。", "可取消检查；不会上传截图、文件夹或设置。");
            try {
                var result = await service.CheckAsync(currentVersion, request.Token);
                if (closed) return;
                update = result;
                if (result == null)
                    SetState("Current", "当前已是最新版本", "当前版本 " + currentVersion + "，暂未发现更新的正式版本。", "稍后可再次检查，或查看 GitHub 版本说明。");
                else
                    SetState("Available", "发现新版本 " + result.Version, ReleaseDetails(result), "下载安装包后会先校验文件。只有点击“退出并安装”才会启动安装。");
            }
            catch (OperationCanceledException) { SetState("Idle", "已取消检查", "当前版本 " + currentVersion, "点击检查更新可重试。"); }
            catch (Exception ex) { SetState("Error", "暂时无法检查更新", "当前版本 " + currentVersion, FriendlyError(ex)); }
            finally { if (Object.ReferenceEquals(operation, request)) operation = null; request.Dispose(); }
        }

        private static string ReleaseDetails(ReleaseUpdate release)
        {
            return "Windows 10 / 11 x64 · 安装包 " + (release.InstallerSize / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB\n更新会保留本机截图、设置和索引。";
        }

        internal void PresentUpdate(ReleaseUpdate release)
        {
            if (closed || operation != null || release == null) return;
            update = release; installerPath = null;
            SetState("Available", "发现新版本 " + release.Version, ReleaseDetails(release), "启动检查发现了此版本。下载和安装仍需你确认；关闭窗口可稍后再处理。");
        }

        internal async Task DownloadInstallerAsync()
        {
            if (closed || operation != null || update == null) return;
            var request = new CancellationTokenSource(); operation = request;
            SetState("Downloading", "正在下载 " + update.Version, ReleaseDetails(update), "下载完成后会校验 SHA-256，可随时取消。");
            progress.Value = 0;
            var reporter = new Progress<int>(delegate(int percent) {
                if (!closed && Object.ReferenceEquals(operation, request)) {
                    progress.Value = percent;
                    status.Text = percent >= 100 ? "下载完成，正在校验安装包…" : "已下载 " + percent.ToString(CultureInfo.InvariantCulture) + "% · 可取消下载";
                }
            });
            try {
                string path = await service.DownloadAsync(update, reporter, request.Token);
                if (closed) return;
                installerPath = path;
                SetState("Ready", update.Version + " 已下载并通过校验", ReleaseDetails(update), "点击“退出并安装”将退出截图晾台并打开 Windows 安装向导。安装完成后，可从快捷方式启动新版。请先保存设置和正在编辑的标注。");
                close.Focus();
            }
            catch (OperationCanceledException) { SetState("Available", "下载已取消", ReleaseDetails(update), "可重新下载安装包。"); }
            catch (Exception ex) { SetState("Available", "安装包下载失败", ReleaseDetails(update), FriendlyError(ex)); }
            finally { if (Object.ReferenceEquals(operation, request)) operation = null; request.Dispose(); }
        }

        internal void RunInstaller()
        {
            if (closed || State != "Ready" || operation != null || update == null || String.IsNullOrEmpty(installerPath)) return;
            primary.IsEnabled = false;
            try {
                UpdateService.VerifyInstallerFile(update, installerPath);
                // Keep a read handle open through launch so the verified file cannot
                // be rewritten or replaced between the hash check and process start.
                using (var stream = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var hash = SHA256.Create()) {
                    string actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    if (!String.Equals(actual, update.InstallerDigest, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("安装包校验失败，请重新下载。");
                    launchInstaller(installerPath);
                }
            }
            catch (Exception ex) {
                installerPath = null;
                SetState("Available", "未能启动安装", ReleaseDetails(update), FriendlyError(ex));
            }
        }

        private void OpenReleasePage(object sender, RoutedEventArgs e)
        {
            try {
                string url = update == null ? "https://github.com/xiaogao007/screenline-windows/releases/latest" : update.ReleaseUri.AbsoluteUri;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception) { status.Text = "无法打开浏览器。请稍后重试，或前往 GitHub 的 screenline-windows 仓库查看 Release。"; }
        }

        private static string FriendlyError(Exception exception)
        {
            if (exception is UpdateException || exception is InvalidDataException) return exception.Message;
            if (exception is Win32Exception) return "安装程序未能启动，可能被系统安全设置阻止。可重新下载后重试。";
            if (exception is IOException || exception is UnauthorizedAccessException) return "无法读取或保存安装包。请检查磁盘空间和文件夹权限后重试。";
            return "连接或操作未能完成。请检查网络后重试，也可打开版本说明手动下载。";
        }
    }
}
