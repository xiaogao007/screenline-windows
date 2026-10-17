using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Screenline
{
    internal sealed class SettingsWindow : Window
    {
        private readonly CheckBox clipboard, folders, reveal, fullscreen, startup;
        private readonly ListBox paths;
        private readonly TextBlock status;
        internal AppSettings Result { get; private set; }
        internal bool StartupEnabled { get { return startup.IsChecked == true; } }
        internal event Action UpdatesRequested;

        internal SettingsWindow(AppSettings settings, bool startupEnabled, string dataRoot)
        {
            Title = "截图晾台 · 设置"; Width = 570; Height = 660; MinWidth = 500; MinHeight = 540;
            Background = Theme.Surface; FontFamily = Theme.Font; Foreground = Theme.Foreground;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var outer = new DockPanel { Margin = new Thickness(28) };
            var bottom = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom); outer.Children.Add(bottom);
            status = new TextBlock { Foreground = Theme.Foreground, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }; bottom.Children.Add(status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = Theme.Button("取消", false); cancel.Margin = new Thickness(0, 0, 8, 0); cancel.IsCancel = true; buttons.Children.Add(cancel);
            var save = Theme.Button("保存设置", true); save.IsDefault = true; save.Click += Save; buttons.Children.Add(save); bottom.Children.Add(buttons);
            var panel = new StackPanel(); outer.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            panel.Children.Add(new TextBlock { Text = "让截图随手可取", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            panel.Children.Add(Note("Ctrl + Alt + T 展开 / 收起    Ctrl + Alt + S 区域截图"));
            folders = Check("自动收集截图文件夹中的新图片", settings.WatchFolders); panel.Children.Add(folders);
            clipboard = Check("收集剪贴板中的纯图片", settings.WatchClipboard); panel.Children.Add(clipboard);
            panel.Children.Add(Note("开启后会在后台读取纯图片；含文字、文件或私密标记的内容会跳过。"));
            reveal = Check("收集到图片后自动展开晾台", settings.RevealOnCapture); panel.Children.Add(reveal);
            fullscreen = Check("应用全屏时暂停自动展开", settings.HideInFullscreen); panel.Children.Add(fullscreen);
            startup = Check("登录 Windows 后启动", startupEnabled); panel.Children.Add(startup);
            panel.Children.Add(new TextBlock { Text = "监控文件夹", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 8) });
            paths = new ListBox { Height = 95, Background = System.Windows.Media.Brushes.White, BorderBrush = Theme.Border };
            if (settings.WatchPaths != null) foreach (var path in settings.WatchPaths) paths.Items.Add(path);
            panel.Children.Add(paths);
            var folderActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
            var add = Theme.Button("添加文件夹…", false); add.Click += delegate {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择要收集新图片的截图文件夹", ShowNewFolderButton = true })
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !paths.Items.Contains(dialog.SelectedPath)) paths.Items.Add(dialog.SelectedPath);
            }; folderActions.Children.Add(add);
            var remove = Theme.Button("移除选中项", false); remove.Margin = new Thickness(8, 0, 0, 0); remove.Click += delegate { if (paths.SelectedItem != null) paths.Items.Remove(paths.SelectedItem); }; folderActions.Children.Add(remove); panel.Children.Add(folderActions);
            panel.Children.Add(Note("只收集开启监控后新增的图片。首次启动不会导入旧文件。"));
            panel.Children.Add(new TextBlock { Text = "本地图库", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 8) });
            panel.Children.Add(Note(dataRoot));
            panel.Children.Add(Note("图片仅保存在本机。移出晾台可撤销，外部源文件始终保留。"));
            panel.Children.Add(new TextBlock { Text = "版本与更新", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 8) });
            panel.Children.Add(Note("当前版本 " + AppVersion.Current + " · 仅在点击检查更新后连接 GitHub。"));
            var updates = Theme.Button("检查更新…", false); updates.HorizontalAlignment = HorizontalAlignment.Left; updates.Margin = new Thickness(0, 8, 0, 0);
            updates.Click += delegate { if (UpdatesRequested != null) UpdatesRequested(); }; panel.Children.Add(updates);
            Content = outer;
        }
        private CheckBox Check(string text, bool value) { return new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 14, 0, 0), FontSize = 14 }; }
        private TextBlock Note(string text) { return new TextBlock { Text = text, Foreground = Theme.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }; }
        private void Save(object sender, RoutedEventArgs e)
        {
            var selected = new List<string>();
            foreach (string path in paths.Items)
            {
                if (!Path.IsPathRooted(path)) { status.Text = "文件夹路径无效：" + path; return; }
                selected.Add(path);
            }
            Result = new AppSettings { WatchClipboard = clipboard.IsChecked == true, WatchFolders = folders.IsChecked == true, RevealOnCapture = reveal.IsChecked == true, HideInFullscreen = fullscreen.IsChecked == true, WatchPaths = selected.ToArray() };
            DialogResult = true;
        }
    }
}
