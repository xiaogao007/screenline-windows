using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Screenline
{
    internal sealed class PreviewWindow : Window
    {
        private readonly InkCanvas ink;
        private readonly Grid canvas;
        private readonly TextBlock status;
        private bool saved = true;
        internal event Func<BitmapSource, bool> SaveToLine;

        internal PreviewWindow(ShotItem item, bool edit)
        {
            Title = "截图晾台 · " + item.DisplayName;
            Width = 1040; Height = 740; MinWidth = 650; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Theme.Surface; FontFamily = Theme.Font;
            var root = new DockPanel { Margin = new Thickness(20) };
            var header = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            var pen = ActionButton(edit ? "结束画笔" : "画笔标注", delegate { ink.EditingMode = ink.EditingMode == InkCanvasEditingMode.Ink ? InkCanvasEditingMode.None : InkCanvasEditingMode.Ink; });
            pen.Click += delegate { pen.Content = ink.EditingMode == InkCanvasEditingMode.Ink ? "结束画笔" : "画笔标注"; };
            header.Children.Add(pen);
            AddColor(header, "蓝色画笔", Color.FromRgb(40, 110, 180));
            AddColor(header, "红色画笔", Color.FromRgb(210, 55, 60));
            AddColor(header, "黄色画笔", Color.FromRgb(235, 175, 35));
            header.Children.Add(ActionButton("撤销笔画", delegate { if (ink.Strokes.Count > 0) ink.Strokes.RemoveAt(ink.Strokes.Count - 1); }));
            header.Children.Add(ActionButton("存回晾台", delegate {
                try {
                    if (SaveToLine == null || !SaveToLine(RenderImage())) { status.Text = "保存失败，标注仍在此窗口。请检查图库空间或选择另存为。"; return; }
                    saved = true; status.Text = "已将标注后的副本加入晾台，原图已保留。";
                } catch (Exception ex) { status.Text = "保存失败：" + ex.Message; }
            }));
            header.Children.Add(ActionButton("另存为…", SaveAs));
            status = new TextBlock { Text = "原图保留。使用画笔标注后，可以存回晾台或另存为 PNG。", Foreground = Theme.Foreground, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
            var bitmap = LoadImage(item.FilePath, 0);
            canvas = new Grid { Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, Background = Brushes.White };
            canvas.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Fill });
            ink = new InkCanvas { Background = Brushes.Transparent, Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, EditingMode = edit ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None };
            ink.DefaultDrawingAttributes = new DrawingAttributes { Color = Color.FromRgb(40, 110, 180), Width = Math.Max(3, bitmap.PixelWidth / 180.0), Height = Math.Max(3, bitmap.PixelWidth / 180.0), FitToCurve = true };
            ink.Strokes.StrokesChanged += delegate { saved = false; };
            canvas.Children.Add(ink);
            var viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = canvas, Margin = new Thickness(12) };
            root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(218, 229, 236)), CornerRadius = new CornerRadius(8), Child = viewbox });
            Content = root;
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                if (saved || ink.Strokes.Count == 0 || Application.Current == null || Application.Current.Dispatcher.HasShutdownStarted) return;
                var dialog = new Window { Owner = this, Title = "保留未保存的标注", Width = 380, Height = 190, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Theme.Surface, FontFamily = Theme.Font, ShowInTaskbar = false };
                var body = new StackPanel { Margin = new Thickness(22) };
                body.Children.Add(new TextBlock { Text = "标注尚未保存。关闭后将放弃这些笔画。", Foreground = Theme.Foreground, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
                var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var keep = Theme.Button("继续编辑", true); keep.IsCancel = true; keep.IsDefault = true; keep.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(keep);
                var discard = Theme.Button("放弃标注", false); discard.Click += delegate { dialog.DialogResult = true; }; actions.Children.Add(discard); body.Children.Add(actions); dialog.Content = body;
                if (dialog.ShowDialog() != true) e.Cancel = true;
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.Escape) Close();
                if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && ink.Strokes.Count > 0) { ink.Strokes.RemoveAt(ink.Strokes.Count - 1); e.Handled = true; }
            };
        }

        internal static BitmapSource LoadImage(string path, int decodeWidth)
        {
            var image = new BitmapImage();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
                image.StreamSource = stream; image.EndInit(); image.Freeze();
            }
            return image;
        }

        private Button ActionButton(string text, Action action)
        {
            var button = Theme.Button(text, false); button.Margin = new Thickness(0, 0, 8, 0);
            button.Click += delegate { action(); }; return button;
        }
        private void AddColor(Panel panel, string name, Color color)
        {
            var button = new Button { Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(color), ToolTip = name, Cursor = Cursors.Hand };
            AutomationProperties.SetName(button, name);
            button.Click += delegate { ink.DefaultDrawingAttributes.Color = color; ink.EditingMode = InkCanvasEditingMode.Ink; };
            panel.Children.Add(button);
        }
        internal BitmapSource RenderImage()
        {
            ink.EditingMode = InkCanvasEditingMode.None;
            canvas.Measure(new Size(canvas.Width, canvas.Height)); canvas.Arrange(new Rect(0, 0, canvas.Width, canvas.Height)); canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)canvas.Width, (int)canvas.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas); bitmap.Freeze(); return bitmap;
        }
        private void SaveAs()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片|*.png", FileName = "截图标注.png", AddExtension = true };
            if (dialog.ShowDialog(this) != true) return;
            try { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(RenderImage())); using (var stream = File.Create(dialog.FileName)) encoder.Save(stream); saved = true; status.Text = "图片已保存。"; }
            catch (Exception ex) { status.Text = "保存失败：" + ex.Message; }
        }
    }
}
