using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Screenline
{
    internal static class Verification
    {
        private static readonly List<string> lines = new List<string>();
        private static int failures;
        internal static int Run(string destination)
        {
            Directory.CreateDirectory(destination);
            string sandbox = Path.Combine(destination, "sandbox-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            lines.Add("Screenline 0.1 · Windows native self-test · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            lines.Add("OS: " + Environment.OSVersion + "; CLR: " + Environment.Version + "; x64: " + Environment.Is64BitProcess);
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ServiceVerification.Run(sandbox, Check);
                UpdateVerification.Run(sandbox, Check);
                Check(CaptureMath.Region(new Point(50, 50), new Point(-50, -20), 100, 100, 200, 200) == new Int32Rect(0, 0, 100, 100), "Crop clamps negative pointer outside selection window");
                Check(CaptureMath.Region(new Point(90, 90), new Point(120, 140), 100, 100, 100, 100) == new Int32Rect(90, 90, 10, 10), "Crop clamps right and bottom edges");
                Check(CaptureMath.Region(new Point(0, 0), new Point(100, 100), 100, 100, 150, 150) == new Int32Rect(0, 0, 150, 150), "Crop preserves scale at 150% DPI");
                var library = new LibraryStore(Path.Combine(sandbox, "render-library"));
                library.ImportBitmap(DemoImage(720, 420, 0));
                library.ImportBitmap(DemoImage(560, 430, 1));
                library.ImportBitmap(DemoImage(380, 500, 2));
                var shelf = new ShelfWindow(library.Items) { AnimationsEnabled = false };
                shelf.Reveal(System.Windows.Forms.Screen.PrimaryScreen, false);
                double shelfWidth = Math.Min(1180, Math.Max(640, SystemParameters.WorkArea.Width));
                shelf.Width = shelfWidth;
                // Hosted Windows runners can use a smaller virtual desktop. Drain the
                // native resize/layout messages before checking the requested width.
                shelf.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { shelf.UpdateLayout(); }));
                Render(shelf, Path.Combine(destination, "shelf-preview.png"), true);
                Check(shelf.IsRevealed && shelf.IsVisible && shelf.AllowsTransparency && Math.Abs(shelf.ActualWidth - shelfWidth) < 1,
                    "Shelf shows native transparent WPF window (requested=" + shelfWidth.ToString(CultureInfo.InvariantCulture) + ", actual=" + shelf.ActualWidth.ToString(CultureInfo.InvariantCulture) + ")");
                bool settingsRaised = false;
                shelf.SettingsRequested += delegate { settingsRaised = true; };
                var settingsButton = Descendants<Button>(shelf).FirstOrDefault(delegate(Button b) { return AutomationProperties.GetName(b) == "设置"; });
                if (settingsButton != null) settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(settingsRaised, "Settings header button raises navigation action");
                bool copyRaised = false;
                shelf.CopyRequested += delegate(ShotItem item) { copyRaised = item != null; };
                var copyButton = Descendants<Button>(shelf).FirstOrDefault(delegate(Button b) { return AutomationProperties.GetName(b).Contains("，单击复制"); });
                if (copyButton != null) copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(copyRaised, "Card copy button dispatches original item without touching system clipboard");
                shelf.Conceal(); Check(!shelf.IsRevealed, "Shelf conceals and releases overlay"); shelf.Close();
                var empty = new ShelfWindow(new ObservableCollection<ShotItem>()) { AnimationsEnabled = false };
                empty.Reveal(System.Windows.Forms.Screen.PrimaryScreen, false); empty.Width = 900; empty.UpdateLayout();
                Render(empty, Path.Combine(destination, "shelf-empty.png"), true);
                Check(Descendants<Button>(empty).Any(delegate(Button b) { return AutomationProperties.GetName(b) == "截取第一张"; }), "Empty shelf has direct capture action"); empty.Close();
                ShotItem small = library.ImportBitmap(DemoImage(120, 80, 3));
                var preview = new PreviewWindow(small, true); preview.Show(); preview.UpdateLayout();
                var ink = Descendants<InkCanvas>(preview).First();
                var points = new StylusPointCollection(new StylusPoint[] { new StylusPoint(10, 10), new StylusPoint(100, 65) });
                ink.Strokes.Add(new Stroke(points));
                var marked = preview.RenderImage();
                Check(marked.PixelWidth == 120 && marked.PixelHeight == 80, "Annotation preserves original small-image resolution");
                var restored = PreviewWindow.LoadImage(small.FilePath, 0);
                Check(restored.PixelWidth == 120 && restored.PixelHeight == 80, "Annotation keeps source image unchanged");
                preview.Width = 670; preview.UpdateLayout(); Render(preview, Path.Combine(destination, "annotation-preview.png"), false);
                Func<BitmapSource, bool> failSave = delegate { return false; }; preview.SaveToLine += failSave;
                var saveAnnotation = Descendants<Button>(preview).First(delegate(Button b) { return AutomationProperties.GetName(b) == "存回晾台"; });
                saveAnnotation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Descendants<TextBlock>(preview).Any(delegate(TextBlock t) { return t.Text.StartsWith("保存失败"); }) && ink.Strokes.Count == 1, "Failed annotation save reports failure and preserves editable strokes");
                preview.SaveToLine -= failSave; preview.SaveToLine += delegate { return true; };
                saveAnnotation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); preview.Close();
                var settings = new SettingsWindow(new AppSettings(), false, library.RootPath); settings.Show(); settings.UpdateLayout();
                Render(settings, Path.Combine(destination, "settings-preview.png"), false);
                Check(settings.StartupEnabled == false, "Startup remains opt-in");
                bool updatesRaised = false; settings.UpdatesRequested += delegate { updatesRaised = true; };
                var updatesButton = Descendants<Button>(settings).FirstOrDefault(delegate(Button button) { return AutomationProperties.GetName(button).StartsWith("检查更新", StringComparison.Ordinal); });
                if (updatesButton != null) updatesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(updatesRaised, "Settings exposes an accessible check-for-updates action and raises its navigation event"); settings.Close();
                app.Shutdown();
            }
            catch (Exception ex) { failures++; lines.Add("FAIL Unexpected exception: " + ex); }
            lines.Add("RESULT: " + (failures == 0 ? "PASS" : "FAIL") + "; failures=" + failures);
            File.WriteAllLines(Path.Combine(destination, "report.txt"), lines.ToArray(), System.Text.Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }
        private static void Check(bool condition, string name) { lines.Add((condition ? "PASS " : "FAIL ") + name); if (!condition) failures++; }

        internal static BitmapSource DemoImage(int width, int height, int variant)
        {
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                Color[] backgrounds = { Color.FromRgb(226, 237, 242), Color.FromRgb(240, 234, 216), Color.FromRgb(214, 229, 219), Color.FromRgb(222, 231, 241) };
                dc.DrawRectangle(new SolidColorBrush(backgrounds[variant % 4]), null, new Rect(0, 0, width, height));
                if (variant % 4 == 0)
                {
                    dc.DrawRectangle(Theme.Accent, null, new Rect(0, 0, width, 44));
                    DrawText(dc, "NOTES  /  记录灵感", 21, Brushes.White, 25, 8);
                    DrawText(dc, "把想法，留在眼前。", width / 21.0, Theme.Accent, 40, height * .27);
                    DrawText(dc, "Capture something worth keeping.", width / 32.0, Theme.Muted, 40, height * .46);
                    for (int i = 0; i < 3; i++) dc.DrawRoundedRectangle(Brushes.White, null, new Rect(40, height * .66 + i * 23, width * (.7 - .12 * i), 9), 4, 4);
                }
                else if (variant % 4 == 1)
                {
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(220, 166, 84)), null, new Point(width * .75, height * .28), width * .10, width * .10);
                    var mountain = new StreamGeometry(); using (var c = mountain.Open()) { c.BeginFigure(new Point(0, height), true, true); c.LineTo(new Point(width * .35, height * .3), true, false); c.LineTo(new Point(width * .60, height * .7), true, false); c.LineTo(new Point(width * .83, height * .48), true, false); c.LineTo(new Point(width, height), true, false); }
                    dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(77, 117, 126)), null, mountain);
                    DrawText(dc, "周末的山间", 28, Brushes.White, 32, height * .82);
                }
                else
                {
                    DrawText(dc, "一小块日常", Math.Max(15, width / 14.0), Theme.Accent, width * .12, height * .12);
                    for (int i = 0; i < 4; i++) { dc.DrawRoundedRectangle(Brushes.White, null, new Rect(width * .1, height * (.30 + i * .14), width * .8, height * .1), 8, 8); dc.DrawEllipse(Theme.Rope, null, new Point(width * .18, height * (.35 + i * .14)), 5, 5); }
                }
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing); bitmap.Freeze(); return bitmap;
        }
        private static void DrawText(DrawingContext dc, string text, double size, Brush brush, double x, double y)
        {
            dc.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, 1.0), new Point(x, y));
        }
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i); if (child is T) yield return (T)child;
                foreach (var item in Descendants<T>(child)) yield return item;
            }
        }
        private static void Render(Window window, string path, bool background)
        {
            int width = (int)Math.Ceiling(window.ActualWidth), height = (int)Math.Ceiling(window.ActualHeight);
            var output = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            if (background)
            {
                var backdrop = new DrawingVisual(); using (var dc = backdrop.RenderOpen()) dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(224, 233, 239), Color.FromRgb(247, 250, 252), 30), null, new Rect(0, 0, width, height));
                output.Render(backdrop);
            }
            output.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(output)); using (var stream = File.Create(path)) encoder.Save(stream);
        }
    }
}
