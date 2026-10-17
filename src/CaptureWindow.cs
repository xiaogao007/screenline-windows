using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Screenline
{
    internal sealed class CaptureWindow : Window
    {
        private readonly BitmapSource source;
        private readonly Canvas overlay;
        private readonly Rectangle selection;
        private Point start;
        private bool selecting;
        internal event Action<BitmapSource> Captured;

        internal CaptureWindow()
        {
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            double scale;
            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96.0;
            using (var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
                source = Native.BitmapSource(bitmap);
            }
            Title = "截图晾台 · 选择截图区域";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Left = bounds.Left / scale; Top = bounds.Top / scale;
            Width = bounds.Width / scale; Height = bounds.Height / scale;
            Cursor = Cursors.Cross;
            var grid = new Grid();
            grid.Children.Add(new Image { Source = source, Stretch = Stretch.Fill });
            overlay = new Canvas { Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)) };
            selection = new Rectangle { Stroke = Brushes.White, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(32, 130, 190, 230)), Visibility = Visibility.Hidden };
            overlay.Children.Add(selection);
            var hint = new Border { Background = new SolidColorBrush(Color.FromRgb(31, 51, 64)), CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 12, 18, 12), Child = new TextBlock { Text = "拖动选择截图区域  ·  Esc 取消", Foreground = Brushes.White, FontSize = 15 } };
            Canvas.SetTop(hint, Math.Max(24, (System.Windows.Forms.Cursor.Position.Y - bounds.Top) / scale - 70));
            Canvas.SetLeft(hint, Math.Max(20, Math.Min(Width - 330, (System.Windows.Forms.Cursor.Position.X - bounds.Left) / scale - 140)));
            overlay.Children.Add(hint);
            grid.Children.Add(overlay);
            Content = grid;
            MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                hint.Visibility = Visibility.Hidden;
                start = e.GetPosition(overlay); selecting = true;
                selection.Visibility = Visibility.Visible;
                CaptureMouse(); UpdateSelection(start);
            };
            MouseMove += delegate(object sender, MouseEventArgs e) { if (selecting) UpdateSelection(e.GetPosition(overlay)); };
            MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                if (!selecting) return;
                selecting = false; ReleaseMouseCapture();
                Int32Rect region = CaptureMath.Region(start, e.GetPosition(overlay), ActualWidth, ActualHeight, source.PixelWidth, source.PixelHeight);
                Close();
                if (region.Width >= 4 && region.Height >= 4)
                {
                    var cropped = new CroppedBitmap(source, region); cropped.Freeze();
                    if (Captured != null) Captured(cropped);
                }
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        }

        private void UpdateSelection(Point end)
        {
            end.X = Math.Max(0, Math.Min(ActualWidth, end.X)); end.Y = Math.Max(0, Math.Min(ActualHeight, end.Y));
            Canvas.SetLeft(selection, Math.Min(start.X, end.X)); Canvas.SetTop(selection, Math.Min(start.Y, end.Y));
            selection.Width = Math.Abs(end.X - start.X); selection.Height = Math.Abs(end.Y - start.Y);
        }
    }

    internal static class CaptureMath
    {
        internal static Int32Rect Region(Point start, Point end, double width, double height, int pixelsX, int pixelsY)
        {
            start.X = Math.Max(0, Math.Min(width, start.X)); start.Y = Math.Max(0, Math.Min(height, start.Y));
            end.X = Math.Max(0, Math.Min(width, end.X)); end.Y = Math.Max(0, Math.Min(height, end.Y));
            int left = (int)Math.Floor(Math.Min(start.X, end.X) * pixelsX / width);
            int top = (int)Math.Floor(Math.Min(start.Y, end.Y) * pixelsY / height);
            int right = Math.Min(pixelsX, (int)Math.Ceiling(Math.Max(start.X, end.X) * pixelsX / width));
            int bottom = Math.Min(pixelsY, (int)Math.Ceiling(Math.Max(start.Y, end.Y) * pixelsY / height));
            return new Int32Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }
    }
}
