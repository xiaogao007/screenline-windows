using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Screenline
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Native.SetProcessDPIAware();
            if (args.Length > 0 && args[0] == "--self-test") return Verification.Run(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "Screenline-tests"));
            if (args.Length > 0 && args[0] == "--clipboard-test") return ClipboardVerification.Run(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "Screenline-clipboard-tests"));
            if (args.Length == 3 && args[0] == "--hold-clipboard") return ClipboardVerification.Hold(args[1], args[2]);
            if (args.Length == 2 && args[0] == "--write-clipboard-fixture") return ClipboardVerification.WriteFixture(args[1]);
            bool first;
            using (var mutex = new Mutex(true, "Local\\Screenline.Desktop.1", out first))
            {
                if (!first) return 0;
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { AppLog.Write(e.Exception); e.Handled = true; };
                    using (var controller = new AppController(app)) { controller.Start(); app.Run(); }
                    return 0;
                }
                catch (Exception ex) { AppLog.Write(ex); return 1; }
            }
        }
    }

    internal static class AppLog
    {
        internal static void Write(Exception exception)
        {
            try { var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screenline"); Directory.CreateDirectory(path); File.AppendAllText(Path.Combine(path, "errors.log"), DateTime.Now.ToString("s") + " " + exception + Environment.NewLine); } catch { }
        }
    }

    internal sealed class AppController : IDisposable
    {
        private readonly Application app;
        private readonly string root;
        private readonly LibraryStore library;
        private AppSettings settings;
        private readonly CaptureSources sources;
        private readonly ShelfWindow shelf;
        private readonly Forms.NotifyIcon tray;
        private readonly HwndSource messageWindow;
        private readonly DispatcherTimer timer;
        private DateTime edgeSince = DateTime.MinValue, leaveSince = DateTime.MinValue, lastReveal = DateTime.MinValue;
        private string revealedScreen;
        private string startupStatus;
        private bool busy, disposed, shuttingDown;
        private System.Drawing.Icon trayIcon;
        private CancellationTokenSource copyCancellation;
        private UpdateWindow updateWindow;

        internal AppController(Application application)
        {
            app = application;
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screenline");
            settings = SettingsStore.Load(root);
            library = new LibraryStore(root);
            shelf = new ShelfWindow(library.Items);
            shelf.CopyRequested += Copy;
            shelf.OpenRequested += delegate(ShotItem item) { OpenPreview(item, false); };
            shelf.EditRequested += delegate(ShotItem item) { OpenPreview(item, true); };
            shelf.SaveRequested += Save;
            shelf.RemoveRequested += delegate(ShotItem item) { if (library.Remove(item)) shelf.SetStatus("已移出晾台 · Ctrl + Z 撤销"); };
            shelf.UndoRequested += delegate { library.UndoLastRemove(); };
            shelf.ImportRequested += Import;
            shelf.CaptureRequested += Capture;
            shelf.SettingsRequested += ShowSettings;
            shelf.HideRequested += delegate { shelf.Conceal(); };
            shelf.FilesDropped += delegate(string[] paths) { foreach (var path in paths) library.ImportFile(path); };
            sources = new CaptureSources(library, settings, app.Dispatcher);
            library.Status += delegate(string message) { startupStatus = message; Report(message); };
            sources.Status += Report;
            library.Load();
            library.Items.CollectionChanged += delegate(object sender, NotifyCollectionChangedEventArgs e) {
                if (e.Action == NotifyCollectionChangedAction.Add && settings.RevealOnCapture && !busy) Reveal(false);
            };
            tray = new Forms.NotifyIcon { Text = "截图晾台 · Ctrl+Alt+T", Visible = false };
            trayIcon = CreateIcon(); tray.Icon = trayIcon;
            tray.DoubleClick += delegate { app.Dispatcher.BeginInvoke(new Action(delegate { Reveal(true); })); };
            var menu = new Forms.ContextMenuStrip();
            AddMenu(menu, "展开 / 收起   Ctrl+Alt+T", Toggle);
            AddMenu(menu, "区域截图   Ctrl+Alt+S", Capture);
            AddMenu(menu, "导入图片…", Import);
            AddMenu(menu, "粘贴剪贴板图片", Paste);
            menu.Items.Add(new Forms.ToolStripSeparator());
            AddMenu(menu, "撤销移出", delegate { library.UndoLastRemove(); });
            AddMenu(menu, "打开本地图库", delegate { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + root + "\"") { UseShellExecute = true }); });
            AddMenu(menu, "设置…", ShowSettings);
            AddMenu(menu, "检查更新…", delegate { ShowUpdates(null); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            AddMenu(menu, "退出截图晾台", delegate { app.Shutdown(); });
            tray.ContextMenuStrip = menu;
            var parameters = new HwndSourceParameters("Screenline.Messages") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) };
            messageWindow = new HwndSource(parameters); messageWindow.AddHook(WindowMessage);
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; timer.Tick += PollPointer;
        }

        internal void Start()
        {
            tray.Visible = true; sources.Start(); timer.Start();
            bool toggle = Native.RegisterHotKey(messageWindow.Handle, 1, 0x4003, 0x54);
            bool capture = Native.RegisterHotKey(messageWindow.Handle, 2, 0x4003, 0x53);
            Reveal(true);
            if (!String.IsNullOrEmpty(startupStatus)) Report(startupStatus);
            if (!toggle || !capture) Report("快捷键被其他应用占用，请从托盘展开或截图。");
        }
        private void AddMenu(Forms.ContextMenuStrip menu, string title, Action action)
        {
            var item = new Forms.ToolStripMenuItem(title);
            item.Click += delegate { app.Dispatcher.BeginInvoke(new Action(delegate { try { action(); } catch (Exception ex) { Report(ex.Message); AppLog.Write(ex); } })); };
            menu.Items.Add(item);
        }
        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0312) { handled = true; if (wParam.ToInt32() == 1) Toggle(); else if (wParam.ToInt32() == 2) Capture(); }
            return IntPtr.Zero;
        }
        private void Toggle() { if (shelf.IsRevealed) shelf.Conceal(); else Reveal(true); }
        private void Reveal(bool activate)
        {
            var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
            if (!activate && settings.HideInFullscreen && Native.IsFullscreen(screen)) return;
            shelf.Reveal(screen, activate); revealedScreen = screen.DeviceName; lastReveal = DateTime.UtcNow; leaveSince = DateTime.MinValue;
        }
        private void PollPointer(object sender, EventArgs e)
        {
            if (busy || shelf.InteractionInProgress || Forms.Control.MouseButtons != Forms.MouseButtons.None) return;
            var point = Forms.Cursor.Position; var screen = Forms.Screen.FromPoint(point);
            var now = DateTime.UtcNow;
            if (settings.HideInFullscreen && Native.IsFullscreen(screen)) { if (shelf.IsRevealed && !shelf.IsActive) shelf.Conceal(); edgeSince = DateTime.MinValue; return; }
            bool atEdge = point.Y <= screen.Bounds.Top + 2;
            if (atEdge) { leaveSince = DateTime.MinValue; if (edgeSince == DateTime.MinValue) edgeSince = now; if ((!shelf.IsRevealed || revealedScreen != screen.DeviceName) && (now - edgeSince).TotalMilliseconds >= 350) Reveal(false); }
            else edgeSince = DateTime.MinValue;
            if (!shelf.IsRevealed) return;
            if (shelf.IsPointerOver || atEdge) { leaveSince = DateTime.MinValue; return; }
            if ((now - lastReveal).TotalMilliseconds < 3000) return;
            if (leaveSince == DateTime.MinValue) leaveSince = now;
            if ((now - leaveSince).TotalMilliseconds > 650) shelf.Conceal();
        }
        private void Report(string text) { shelf.SetStatus(text); }
        private async void Copy(ShotItem item)
        {
            if (disposed) return;
            var request = new CancellationTokenSource();
            if (copyCancellation != null) copyCancellation.Cancel();
            copyCancellation = request;
            try {
                var image = PreviewWindow.LoadImage(item.FilePath, 0);
                Report("正在复制图片…");
                await ImageClipboardWriter.CopyAsync(image, item.FilePath, messageWindow.Handle, request.Token);
                if (!disposed && Object.ReferenceEquals(copyCancellation, request)) Report("已复制图片");
            }
            catch (OperationCanceledException) { }
            catch (ClipboardBusyException) {
                if (!disposed && Object.ReferenceEquals(copyCancellation, request)) Report("剪贴板正被其他程序占用，请稍后再试。");
            }
            catch (Exception ex) {
                if (!disposed && Object.ReferenceEquals(copyCancellation, request)) { Report("复制失败：" + ex.Message); AppLog.Write(ex); }
            }
            finally {
                if (Object.ReferenceEquals(copyCancellation, request)) copyCancellation = null;
                request.Dispose();
            }
        }
        private void Paste()
        {
            try { if (Clipboard.ContainsImage()) { var image = Clipboard.GetImage(); if (image != null) library.ImportBitmap(image); } else Report("剪贴板中没有图片。"); }
            catch (Exception ex) { Report("暂时无法读取剪贴板：" + ex.Message); }
        }
        private void Import()
        {
            busy = true;
            try { var dialog = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif", Multiselect = true, Title = "将图片挂上晾台" }; if (dialog.ShowDialog() == true) foreach (string path in dialog.FileNames) library.ImportFile(path); }
            finally { busy = false; Reveal(true); }
        }
        private void Save(ShotItem item)
        {
            busy = true;
            try {
                var dialog = new SaveFileDialog { FileName = item.DisplayName, Filter = "图片|*" + Path.GetExtension(item.FilePath), DefaultExt = Path.GetExtension(item.FilePath), AddExtension = true };
                if (dialog.ShowDialog() == true) {
                    if (String.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(item.FilePath), StringComparison.OrdinalIgnoreCase)) { Report("图片已在该位置。"); return; }
                    File.Copy(item.FilePath, dialog.FileName, true); Report("副本已保存，图片仍在晾台。");
                }
            } catch (Exception ex) { Report("保存失败：" + ex.Message); }
            finally { busy = false; }
        }
        private void OpenPreview(ShotItem item, bool edit)
        {
            try {
                shelf.Conceal();
                var window = new PreviewWindow(item, edit);
                window.SaveToLine += delegate(BitmapSource image) { return library.ImportBitmap(image) != null; };
                window.Show();
            } catch (Exception ex) { Report("无法打开图片：" + ex.Message); Reveal(true); }
        }
        private void Capture()
        {
            if (busy) return;
            busy = true; shelf.Conceal();
            var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            delay.Tick += delegate {
                delay.Stop();
                try {
                    var window = new CaptureWindow();
                    window.Captured += delegate(BitmapSource image) { library.ImportBitmap(image); Reveal(true); };
                    window.Closed += delegate { busy = false; };
                    window.Show(); window.Activate();
                } catch (Exception ex) { busy = false; Report("截图失败：" + ex.Message); Reveal(true); }
            }; delay.Start();
        }
        private bool IsStartupEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")) return key != null && key.GetValue("Screenline") != null;
        }
        private void ShowSettings()
        {
            busy = true; shelf.Conceal();
            try {
                var window = new SettingsWindow(settings, IsStartupEnabled(), root);
                window.UpdatesRequested += delegate { ShowUpdates(window); };
                if (window.ShowDialog() == true) {
                    var candidate = window.Result; SettingsStore.Save(root, candidate); settings = candidate; sources.Restart(settings);
                    try {
                        using (var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
                            if (window.StartupEnabled) key.SetValue("Screenline", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\""); else key.DeleteValue("Screenline", false);
                        Report("设置已保存。");
                    } catch (Exception ex) { Report("设置已保存，但开机启动设置失败：" + ex.Message); }
                }
            } catch (Exception ex) { Report("无法保存设置：" + ex.Message); }
            finally { busy = false; if (!disposed && !shuttingDown) Reveal(true); }
        }
        private void ShowUpdates(Window owner)
        {
            if (disposed) return;
            if (updateWindow != null) { updateWindow.Activate(); return; }
            bool wasBusy = busy;
            busy = true; shelf.Conceal();
            try {
                updateWindow = new UpdateWindow(delegate(string installer) {
                    var process = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
                    if (process == null) throw new InvalidOperationException("未能启动安装程序。");
                    process.Dispose();
                    shuttingDown = true;
                    app.Shutdown();
                });
                if (owner != null) { updateWindow.Owner = owner; updateWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
                updateWindow.ShowDialog();
            }
            catch (Exception ex) { if (!disposed) Report("无法打开更新窗口：" + ex.Message); }
            finally { updateWindow = null; busy = wasBusy; if (!disposed && !shuttingDown && !wasBusy) Reveal(true); }
        }
        private System.Drawing.Icon CreateIcon()
        {
            using (var bitmap = new System.Drawing.Bitmap(32, 32))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.Clear(System.Drawing.Color.Transparent);
                using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(36, 70, 93))) graphics.FillEllipse(brush, 1, 1, 30, 30);
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.White, 1.8f)) { graphics.DrawLine(pen, 6, 10, 26, 10); graphics.DrawRectangle(pen, 9, 13, 14, 10); graphics.DrawLine(pen, 16, 8, 16, 15); }
                IntPtr handle = bitmap.GetHicon();
                try { using (var temporary = System.Drawing.Icon.FromHandle(handle)) return (System.Drawing.Icon)temporary.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (copyCancellation != null) copyCancellation.Cancel();
            if (updateWindow != null) updateWindow.Close();
            timer.Stop(); sources.Dispose(); Native.UnregisterHotKey(messageWindow.Handle, 1); Native.UnregisterHotKey(messageWindow.Handle, 2); messageWindow.Dispose();
            tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); shelf.Close();
        }
    }
}
