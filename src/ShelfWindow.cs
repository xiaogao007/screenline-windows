using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Screenline
{
    public sealed class ShelfWindow : Window
    {
        private readonly ObservableCollection<ShotItem> _items;
        private readonly Grid _root;
        private readonly StackPanel _cardsPanel;
        private readonly ScrollViewer _scroll;
        private readonly Border _empty;
        private readonly Border _brand;
        private readonly Border _statusPanel;
        private readonly TextBlock _status;
        private readonly TextBlock _count;
        private readonly List<PhotoCard> _cards = new List<PhotoCard>();
        private readonly DispatcherTimer _statusTimer;
        private readonly RopeVisual _rope;
        private int _interactions;
        private bool _allowActivation;
        private bool _revealed;
        private bool _animationsEnabled = true;
        private HwndSource _source;

        public event Action<ShotItem> CopyRequested;
        public event Action<ShotItem> OpenRequested;
        public event Action<ShotItem> EditRequested;
        public event Action<ShotItem> SaveRequested;
        public event Action<ShotItem> RemoveRequested;
        public event Action ImportRequested;
        public event Action CaptureRequested;
        public event Action SettingsRequested;
        public event Action HideRequested;
        public event Action UndoRequested;
        public event Action<string[]> FilesDropped;

        public bool IsRevealed { get { return _revealed; } }
        public bool IsPointerOver { get { return IsMouseOver || _interactions > 0; } }
        public bool InteractionInProgress { get { return _interactions > 0; } }
        public bool AnimationsEnabled
        {
            get { return _animationsEnabled && SystemParameters.ClientAreaAnimation; }
            set { _animationsEnabled = value; }
        }

        public ShelfWindow(ObservableCollection<ShotItem> items)
        {
            _items = items;
            Title = "截图晾台 · Screenline";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Height = 278;
            Width = 1200;
            FontFamily = Theme.Font;
            Foreground = Theme.Foreground;
            AllowDrop = true;
            AutomationProperties.SetName(this, "截图晾台：截图暂存与分享");

            _root = new Grid { Background = null, ClipToBounds = true };
            Content = _root;
            _rope = new RopeVisual { IsHitTestVisible = false };
            _root.Children.Add(_rope);

            Border brand = new Border { Background = Brushes.White, BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(13, 6, 12, 6), Margin = new Thickness(24, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            _brand = brand;
            StackPanel brandLine = new StackPanel { Orientation = Orientation.Horizontal };
            TextBlock mark = Theme.Text("⌁", 23, Theme.Accent);
            mark.FontWeight = FontWeights.Bold;
            mark.Margin = new Thickness(0, -4, 9, 0);
            brandLine.Children.Add(mark);
            TextBlock title = Theme.Text("截图晾台", 13, Theme.Accent);
            title.FontWeight = FontWeights.SemiBold;
            brandLine.Children.Add(title);
            TextBlock english = Theme.Text("SCREENLINE", 9, Theme.Muted);
            english.Margin = new Thickness(9, 2, 13, 0);
            brandLine.Children.Add(english);
            _count = Theme.Text("0 张", 11, Theme.Muted);
            brandLine.Children.Add(_count);
            brand.Child = brandLine;
            brand.ToolTip = "把截图挂在屏幕上沿，随手复制、标注与分享";
            _root.Children.Add(brand);

            StackPanel toolbar = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 24, 0) };
            toolbar.Children.Add(HeaderAction("截图", "\uE722", "截取屏幕区域", true, delegate { Raise(CaptureRequested); }));
            toolbar.Children.Add(HeaderAction("导入", "\uE8B5", "导入图片，也可以把图片拖到晾台上", false, delegate { Raise(ImportRequested); }));
            toolbar.Children.Add(HeaderAction("设置", "\uE713", "打开设置", false, delegate { Raise(SettingsRequested); }));
            toolbar.Children.Add(HeaderAction("收起", "\uE70E", "收起晾台（Esc）", false, delegate { Raise(HideRequested); }));
            _root.Children.Add(toolbar);

            _cardsPanel = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            _scroll = new ScrollViewer { Content = _cardsPanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Top,
                Background = null, Margin = new Thickness(18, 38, 18, 30), Focusable = false,
                PanningMode = PanningMode.HorizontalOnly };
            AutomationProperties.SetName(_scroll, "截图晾台，水平滚动查看更多图片");
            _scroll.ScrollChanged += delegate { UpdatePegOffsets(); };
            _scroll.PreviewMouseWheel += OnMouseWheel;
            _root.Children.Add(_scroll);

            _empty = CreateEmptyState();
            _root.Children.Add(_empty);

            _status = Theme.Text("单击复制  ·  长按标注  ·  拖出分享", 11, Theme.Muted);
            _statusPanel = new Border { Background = new SolidColorBrush(Color.FromArgb(245, 237, 244, 247)),
                BorderBrush = Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11),
                Padding = new Thickness(13, 4, 13, 4), Child = _status, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4), IsHitTestVisible = false };
            _root.Children.Add(_statusPanel);
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.8) };
            _statusTimer.Tick += delegate { _statusTimer.Stop(); _status.Text = "单击复制  ·  长按标注  ·  拖出分享"; _status.Foreground = Theme.Muted; };

            _items.CollectionChanged += OnItemsChanged;
            SourceInitialized += OnSourceInitialized;
            SizeChanged += delegate { UpdatePegOffsets(); };
            PreviewKeyDown += OnKeyDown;
            DragEnter += OnDragOver;
            DragOver += OnDragOver;
            Drop += OnDrop;
            Closed += delegate { _items.CollectionChanged -= OnItemsChanged; _statusTimer.Stop(); DisposeCards(); };
            RebuildCards();
        }

        private Button HeaderAction(string label, string glyph, string tooltip, bool primary, Action action)
        {
            Button button = Theme.ActionButton(label, glyph, primary);
            button.Margin = new Thickness(5, 0, 0, 0);
            button.ToolTip = tooltip;
            button.Click += delegate { RunInteraction(action); };
            return button;
        }

        private Border CreateEmptyState()
        {
            Border panel = new Border { Width = 476, Padding = new Thickness(24, 18, 24, 17),
                Background = Brushes.White, BorderBrush = Theme.Border, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(13), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 83, 0, 0),
                Effect = new DropShadowEffect { Color = Color.FromRgb(26, 49, 64), BlurRadius = 24, ShadowDepth = 5, Opacity = 0.12 } };
            StackPanel content = new StackPanel();
            TextBlock title = Theme.Text("给截图一个随手可取的位置", 17, Theme.Accent);
            title.FontWeight = FontWeights.SemiBold;
            title.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(title);
            TextBlock detail = Theme.Text("截下一块屏幕，或把图片拖到这里。", 12, Theme.Muted);
            detail.Margin = new Thickness(0, 8, 0, 15);
            detail.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(detail);
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            Button capture = Theme.ActionButton("截取第一张", "\uE722", true);
            capture.Click += delegate { RunInteraction(delegate { Raise(CaptureRequested); }); };
            actions.Children.Add(capture);
            Button import = Theme.Button("导入图片", false);
            import.Margin = new Thickness(9, 0, 0, 0);
            import.Click += delegate { RunInteraction(delegate { Raise(ImportRequested); }); };
            actions.Children.Add(import);
            content.Children.Add(actions);
            panel.Child = content;
            AutomationProperties.SetName(panel, "晾台还没有图片，可截图或导入图片");
            return panel;
        }

        public void Reveal(Forms.Screen screen, bool activate)
        {
            if (screen == null) screen = Forms.Screen.PrimaryScreen;
            Matrix device = Matrix.Identity;
            if (_source != null && _source.CompositionTarget != null) device = _source.CompositionTarget.TransformFromDevice;
            else
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                try
                {
                    double scale = dc == IntPtr.Zero ? 1.0 : GetDeviceCaps(dc, 88) / 96.0;
                    if (scale <= 0) scale = 1;
                    device = new Matrix(1 / scale, 0, 0, 1 / scale, 0, 0);
                }
                finally { if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc); }
            }
            Point origin = device.Transform(new Point(screen.Bounds.Left, screen.Bounds.Top));
            Left = origin.X;
            Top = origin.Y;
            Width = Math.Max(640, screen.Bounds.Width * device.M11);
            _allowActivation = activate;
            ShowActivated = activate;
            _revealed = true;
            if (!IsVisible) Show();
            if (activate) { Activate(); Focus(); }
            UpdatePegOffsets();
            if (AnimationsEnabled)
            {
                _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            }
            else { _root.BeginAnimation(OpacityProperty, null); _root.Opacity = 1; }
        }

        public void Conceal()
        {
            _revealed = false;
            foreach (PhotoCard card in _cards) card.CancelGesture();
            Hide();
        }

        public void SetStatus(string message)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action<string>(SetStatus), message); return; }
            _status.Text = String.IsNullOrWhiteSpace(message) ? "单击复制  ·  长按标注  ·  拖出分享" : message;
            _status.Foreground = Theme.Accent;
            _statusTimer.Stop();
            _statusTimer.Start();
        }

        private void OnItemsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RebuildCards)); return; }
            RebuildCards();
        }

        private void DisposeCards()
        {
            foreach (PhotoCard card in _cards) card.Dispose();
            _cards.Clear();
        }

        private void RebuildCards()
        {
            double offset = _scroll.HorizontalOffset;
            DisposeCards();
            _cardsPanel.Children.Clear();
            foreach (ShotItem item in _items.OrderByDescending(delegate(ShotItem i) { return i.AddedAt; }))
            {
                PhotoCard card = new PhotoCard(this, item);
                _cards.Add(card);
                _cardsPanel.Children.Add(card);
            }
            _count.Text = _items.Count.ToString() + " 张";
            _empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _scroll.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate { _scroll.ScrollToHorizontalOffset(offset); UpdatePegOffsets(); }));
        }

        private void UpdatePegOffsets()
        {
            double width = ActualWidth > 0 ? ActualWidth : Width;
            double total = _cards.Count * 214;
            double start = Math.Max(18, (width - total) / 2);
            double sag = Math.Min(18, width * 0.018);
            for (int index = 0; index < _cards.Count; index++)
            {
                double x = start + index * 214 + 107 - _scroll.HorizontalOffset;
                double f = Math.Max(0, Math.Min(1, x / Math.Max(1, width)));
                double top = 4 * sag * f * (1 - f);
                _cards[index].Margin = new Thickness(12, top, 12, 0);
            }
            _rope.InvalidateVisual();
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_scroll.ExtentWidth > _scroll.ViewportWidth)
            {
                _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Raise(HideRequested); e.Handled = true; }
            else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            { Raise(UndoRequested); e.Handled = true; }
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
            else { e.Effects = DragDropEffects.None; e.Handled = true; }
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            e.Handled = true;
            e.Effects = DragDropEffects.Copy;
            if (files != null && files.Length > 0 && FilesDropped != null)
                RunInteraction(delegate { FilesDropped(files); });
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (_source != null) _source.AddHook(WindowProc);
        }

        private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0021 && !_allowActivation) { handled = true; return new IntPtr(3); }
            if (message == 0x0084)
            {
                int packed = unchecked((int)lParam.ToInt64());
                Point point = PointFromScreen(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
                IInputElement hit = _root.InputHitTest(point);
                if (!IsInteractiveHit(hit)) { handled = true; return new IntPtr(-1); }
            }
            return IntPtr.Zero;
        }

        private bool IsInteractiveHit(IInputElement hit)
        {
            DependencyObject node = hit as DependencyObject;
            while (node != null && !Object.ReferenceEquals(node, _root))
            {
                if (node is System.Windows.Controls.Primitives.ButtonBase || node is System.Windows.Controls.Primitives.ScrollBar ||
                    Object.ReferenceEquals(node, _brand) || Object.ReferenceEquals(node, _empty)) return true;
                if (node is Visual || node is System.Windows.Media.Media3D.Visual3D) node = VisualTreeHelper.GetParent(node);
                else node = LogicalTreeHelper.GetParent(node);
            }
            return false;
        }

        internal void BeginInteraction() { _interactions++; }
        internal void EndInteraction() { _interactions = Math.Max(0, _interactions - 1); }
        internal void RunInteraction(Action action)
        {
            BeginInteraction();
            try { if (action != null) action(); }
            finally { EndInteraction(); }
        }
        private static void Raise(Action action) { if (action != null) action(); }
        internal void Copy(ShotItem item) { if (CopyRequested != null) CopyRequested(item); }
        internal void Open(ShotItem item) { if (OpenRequested != null) OpenRequested(item); }
        internal void Edit(ShotItem item) { if (EditRequested != null) EditRequested(item); }
        internal void Save(ShotItem item) { if (SaveRequested != null) SaveRequested(item); }
        internal void Remove(ShotItem item) { if (RemoveRequested != null) RemoveRequested(item); }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);

        private sealed class RopeVisual : FrameworkElement
        {
            protected override void OnRender(DrawingContext dc)
            {
                double width = ActualWidth;
                if (width <= 0) return;
                double sag = Math.Min(18, width * 0.018);
                StreamGeometry rope = new StreamGeometry();
                using (StreamGeometryContext path = rope.Open())
                { path.BeginFigure(new Point(-14, 50), false, false); path.QuadraticBezierTo(new Point(width / 2, 50 + 2 * sag), new Point(width + 14, 50), true, false); }
                rope.Freeze();
                LinearGradientBrush fade = new LinearGradientBrush();
                fade.StartPoint = new Point(0, 0); fade.EndPoint = new Point(1, 0);
                fade.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
                fade.GradientStops.Add(new GradientStop(Color.FromRgb(128, 148, 160), 0.06));
                fade.GradientStops.Add(new GradientStop(Color.FromRgb(128, 148, 160), 0.94));
                fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                dc.PushTransform(new TranslateTransform(0, 1));
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(38, 24, 45, 58)), 2.6), rope);
                dc.Pop();
                dc.DrawGeometry(null, new Pen(fade, 1.3), rope);
                dc.PushTransform(new TranslateTransform(0, -0.5));
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)), 0.4), rope);
                dc.Pop();
            }
        }

        private sealed class PhotoCard : Grid, IDisposable
        {
            private readonly ShelfWindow _owner;
            private readonly ShotItem _item;
            private readonly Button _imageButton;
            private readonly Image _image;
            private readonly TextBlock _caption;
            private readonly TextBlock _dimensions;
            private readonly Border _photo;
            private readonly RotateTransform _rotation;
            private readonly ScaleTransform _scale;
            private readonly DispatcherTimer _singleClick;
            private readonly DispatcherTimer _hold;
            private readonly ContextMenu _contextMenu;
            private Point _downPoint;
            private bool _pressed;
            private bool _holdFired;
            private bool _doubleClick;
            private bool _dragging;
            private bool _disposed;
            private bool _activeGesture;
            private readonly double _tilt;

            public PhotoCard(ShelfWindow owner, ShotItem item)
            {
                _owner = owner; _item = item;
                Width = 190; Height = 174;
                Background = null;
                VerticalAlignment = VerticalAlignment.Top;
                int hash = 0;
                foreach (char c in item.Id ?? String.Empty) hash = unchecked(hash * 31 + c);
                _tilt = (Math.Abs((long)hash) % 7 - 3) * 0.65;
                _rotation = new RotateTransform(_tilt);
                _scale = new ScaleTransform(1, 1);
                TransformGroup transforms = new TransformGroup(); transforms.Children.Add(_rotation); transforms.Children.Add(_scale);
                RenderTransform = transforms;
                RenderTransformOrigin = new Point(0.5, 0.065);

                _image = new Image { Stretch = Stretch.Uniform, Width = 170, Height = 104,
                    SnapsToDevicePixels = true, IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
                Grid photoBody = new Grid();
                photoBody.RowDefinitions.Add(new RowDefinition { Height = new GridLength(104) });
                photoBody.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
                Border imageWell = new Border { Background = Theme.Surface, CornerRadius = new CornerRadius(4),
                    Child = _image, ClipToBounds = true, Width = 170, Height = 104 };
                photoBody.Children.Add(imageWell);
                _caption = Theme.Text(item.DisplayName, 10, Theme.Foreground);
                _caption.TextTrimming = TextTrimming.CharacterEllipsis;
                _caption.Margin = new Thickness(1, 2, 56, 0);
                Grid.SetRow(_caption, 1);
                photoBody.Children.Add(_caption);
                _photo = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
                    BorderThickness = new Thickness(1), Padding = new Thickness(5), CornerRadius = new CornerRadius(8), Child = photoBody,
                    Effect = new DropShadowEffect { Color = Color.FromRgb(20, 41, 54), BlurRadius = 17, ShadowDepth = 5, Opacity = 0.20 } };
                _imageButton = Theme.Button(item.DisplayName, false);
                _imageButton.Content = _photo;
                _imageButton.Padding = new Thickness(0);
                _imageButton.BorderThickness = new Thickness(0);
                _imageButton.Background = Brushes.Transparent;
                _imageButton.Width = 182;
                _imageButton.Height = 140;
                _imageButton.HorizontalAlignment = HorizontalAlignment.Center;
                _imageButton.VerticalAlignment = VerticalAlignment.Top;
                _imageButton.Margin = new Thickness(0, 18, 0, 0);
                _imageButton.ToolTip = item.DisplayName + "\n单击复制 · 双击打开 · 长按标注 · 拖出分享\nEnter 复制 · Delete 移出晾台";
                AutomationProperties.SetName(_imageButton, item.DisplayName + "，单击复制，双击打开，长按标注");
                _imageButton.PreviewMouseLeftButtonDown += OnMouseDown;
                _imageButton.PreviewMouseLeftButtonUp += OnMouseUp;
                _imageButton.PreviewMouseMove += OnMouseMove;
                _imageButton.LostMouseCapture += delegate { if (_pressed && !_dragging) CancelGesture(); };
                _imageButton.Click += delegate { _owner.RunInteraction(delegate { _owner.Copy(_item); }); };
                _imageButton.PreviewKeyDown += OnCardKeyDown;
                _imageButton.GotKeyboardFocus += delegate { _photo.BorderBrush = Theme.Accent; _photo.BorderThickness = new Thickness(2); _photo.Padding = new Thickness(4); };
                _imageButton.LostKeyboardFocus += delegate { _photo.BorderBrush = Brushes.White; _photo.BorderThickness = new Thickness(1); _photo.Padding = new Thickness(5); };
                Children.Add(_imageButton);

                Canvas peg = new Canvas { Width = 12, Height = 28, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                    Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Opacity = 0.22 } };
                Border wood = new Border { Width = 9, Height = 27, CornerRadius = new CornerRadius(2),
                    Background = new LinearGradientBrush(Color.FromRgb(226, 202, 162), Color.FromRgb(180, 145, 100), 0),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(169, 135, 91)), BorderThickness = new Thickness(0.7) };
                Canvas.SetLeft(wood, 1); peg.Children.Add(wood);
                System.Windows.Shapes.Line seam = new System.Windows.Shapes.Line { X1 = 5.4, X2 = 5.4, Y1 = 2, Y2 = 24,
                    Stroke = new SolidColorBrush(Color.FromArgb(85, 110, 81, 46)), StrokeThickness = 0.8 };
                peg.Children.Add(seam);
                System.Windows.Shapes.Rectangle spring = new System.Windows.Shapes.Rectangle { Width = 11, Height = 4, RadiusX = 1, RadiusY = 1,
                    Fill = new SolidColorBrush(Color.FromRgb(121, 125, 125)), Stroke = new SolidColorBrush(Color.FromRgb(211, 211, 203)), StrokeThickness = 0.6 };
                Canvas.SetLeft(spring, 0); Canvas.SetTop(spring, 12); peg.Children.Add(spring);
                Children.Add(peg);

                StackPanel tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 128, 10, 0) };
                Button save = TinyButton("\uE74E", "另存图片", delegate { _owner.Save(_item); });
                Button remove = TinyButton("\uE711", "移出晾台（可撤销，原文件保留）", delegate { _owner.Remove(_item); });
                tools.Children.Add(save); tools.Children.Add(remove); Children.Add(tools);

                _dimensions = Theme.Text("", 9, Theme.Muted);
                _dimensions.HorizontalAlignment = HorizontalAlignment.Center;
                _dimensions.VerticalAlignment = VerticalAlignment.Top;
                _dimensions.Margin = new Thickness(0, 162, 0, 0);
                _dimensions.IsHitTestVisible = false;
                Children.Add(_dimensions);

                ContextMenu menu = new ContextMenu { FontFamily = Theme.Font, FontSize = 12 };
                _contextMenu = menu;
                AddMenu(menu, "复制图片", delegate { _owner.Copy(_item); });
                AddMenu(menu, "打开原图", delegate { _owner.Open(_item); });
                AddMenu(menu, "标注图片…", delegate { _owner.Edit(_item); });
                AddMenu(menu, "另存图片…", delegate { _owner.Save(_item); });
                menu.Items.Add(new Separator());
                AddMenu(menu, "移出晾台", delegate { _owner.Remove(_item); });
                menu.Opened += delegate { CancelGesture(); _owner.BeginInteraction(); };
                menu.Closed += delegate { _owner.EndInteraction(); };
                _imageButton.ContextMenu = menu;
                MouseEnter += delegate { AnimateHover(true); };
                MouseLeave += delegate { AnimateHover(false); };
                _singleClick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime() + 25) };
                _singleClick.Tick += delegate
                {
                    _singleClick.Stop();
                    try { if (!_disposed) _owner.Copy(_item); }
                    finally { EndGesture(); }
                };
                _hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _hold.Tick += OnHold;
                item.PropertyChanged += OnItemChanged;
                RefreshImage();
            }

            private Button TinyButton(string glyph, string label, Action action)
            {
                Button button = Theme.Button(label, false);
                TextBlock icon = Theme.Text(glyph, 10, Theme.Accent);
                icon.FontFamily = new FontFamily("Segoe MDL2 Assets");
                button.Content = icon;
                button.Padding = new Thickness(0); button.MinHeight = 22; button.Width = 24; button.Height = 22;
                button.BorderThickness = new Thickness(0); button.Background = Brushes.Transparent;
                button.ToolTip = label;
                button.Click += delegate { CancelGesture(); _singleClick.Stop(); _owner.RunInteraction(action); };
                return button;
            }

            private void AddMenu(ContextMenu menu, string label, Action action)
            {
                MenuItem item = new MenuItem { Header = label };
                item.Click += delegate { CancelGesture(); _singleClick.Stop(); _owner.RunInteraction(action); };
                menu.Items.Add(item);
            }

            private void RefreshImage()
            {
                _image.Source = _item.Thumbnail;
                _caption.Text = _item.DisplayName;
                _dimensions.Text = _item.PixelWidth > 0 ? _item.PixelWidth.ToString() + " × " + _item.PixelHeight.ToString() : "图片";
            }

            private void OnItemChanged(object sender, PropertyChangedEventArgs e)
            {
                if (Dispatcher.CheckAccess()) RefreshImage();
                else Dispatcher.BeginInvoke(new Action(RefreshImage));
            }

            private void AnimateHover(bool over)
            {
                if (!_owner.AnimationsEnabled)
                { _rotation.Angle = over ? 0 : _tilt; _scale.ScaleX = 1; _scale.ScaleY = 1; return; }
                Duration duration = new Duration(TimeSpan.FromMilliseconds(150));
                _rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(over ? 0 : _tilt, duration));
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(over ? 1.025 : 1, duration));
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(over ? 1.025 : 1, duration));
            }

            private void OnMouseDown(object sender, MouseButtonEventArgs e)
            {
                if (_disposed) return;
                e.Handled = true;
                _imageButton.Focus();
                _singleClick.Stop();
                _holdFired = false;
                _doubleClick = e.ClickCount >= 2;
                _pressed = true;
                _downPoint = e.GetPosition(this);
                if (!_activeGesture) { _activeGesture = true; _owner.BeginInteraction(); }
                _imageButton.CaptureMouse();
                if (_doubleClick)
                { _hold.Stop(); _owner.Open(_item); }
                else _hold.Start();
            }

            private void OnMouseUp(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                bool copy = _pressed && !_dragging && !_holdFired && !_doubleClick;
                _pressed = false;
                _hold.Stop();
                _imageButton.ReleaseMouseCapture();
                if (copy) _singleClick.Start();
                else EndGesture();
            }

            private void OnHold(object sender, EventArgs e)
            {
                _hold.Stop();
                if (!_pressed || _dragging || _disposed) return;
                _holdFired = true;
                _pressed = false;
                _imageButton.ReleaseMouseCapture();
                try { _owner.Edit(_item); }
                finally { EndGesture(); }
            }

            private void OnMouseMove(object sender, MouseEventArgs e)
            {
                if (!_pressed || _dragging || _holdFired || e.LeftButton != MouseButtonState.Pressed) return;
                Point current = e.GetPosition(this);
                if (Math.Abs(current.X - _downPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(current.Y - _downPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _dragging = true; _pressed = false; _hold.Stop(); _singleClick.Stop();
                _imageButton.ReleaseMouseCapture();
                try
                {
                    if (!File.Exists(_item.FilePath)) { _owner.SetStatus("原文件已不在此位置，可移出后重新导入"); return; }
                    DataObject data = new DataObject();
                    data.SetData(DataFormats.FileDrop, new string[] { _item.FilePath });
                    try
                    {
                        BitmapImage full = new BitmapImage();
                        full.BeginInit(); full.CacheOption = BitmapCacheOption.OnLoad; full.UriSource = new Uri(_item.FilePath, UriKind.Absolute); full.EndInit(); full.Freeze();
                        data.SetData(DataFormats.Bitmap, full);
                    }
                    catch { if (_item.Thumbnail != null) data.SetData(DataFormats.Bitmap, _item.Thumbnail); }
                    Opacity = 0.6;
                    DragDrop.DoDragDrop(_imageButton, data, DragDropEffects.Copy);
                }
                finally { Opacity = 1; _dragging = false; EndGesture(); }
            }

            private void OnCardKeyDown(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter || e.Key == Key.Space)
                { CancelGesture(); _owner.RunInteraction(delegate { _owner.Copy(_item); }); e.Handled = true; }
                else if (e.Key == Key.Delete)
                { CancelGesture(); _owner.RunInteraction(delegate { _owner.Remove(_item); }); e.Handled = true; }
            }

            private void EndGesture()
            {
                if (_activeGesture) { _activeGesture = false; _owner.EndInteraction(); }
            }

            public void CancelGesture()
            {
                _pressed = false;
                _hold.Stop();
                _singleClick.Stop();
                _imageButton.ReleaseMouseCapture();
                EndGesture();
            }

            public void Dispose()
            {
                _disposed = true;
                CancelGesture();
                _singleClick.Stop();
                _contextMenu.IsOpen = false;
                _item.PropertyChanged -= OnItemChanged;
            }

            [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
        }
    }
}
