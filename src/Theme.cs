using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;

namespace Screenline
{
    internal static class Theme
    {
        public static readonly SolidColorBrush Accent = Brush("#24465D");
        public static readonly SolidColorBrush Foreground = Brush("#1F3340");
        public static readonly SolidColorBrush Muted = Brush("#59717F");
        public static readonly SolidColorBrush Surface = Brush("#EDF4F7");
        public static readonly SolidColorBrush Border = Brush("#D7E3E9");
        public static readonly SolidColorBrush Rope = Brush("#8094A0");
        public static readonly FontFamily Font = new FontFamily("Segoe UI, Microsoft YaHei UI");

        public static SolidColorBrush Brush(string color)
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        public static TextBlock Text(string value, double size, Brush foreground)
        {
            return new TextBlock { Text = value, FontFamily = Font, FontSize = size, Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center };
        }

        public static TextBlock Text(string value, double size)
        {
            return Text(value, size, Foreground);
        }

        public static Button Button(string label, bool primary)
        {
            Button button = new Button { Content = label, FontFamily = Font, FontSize = 12,
                Padding = new Thickness(12, 6, 12, 6), MinHeight = 32, Cursor = System.Windows.Input.Cursors.Hand,
                Background = primary ? Accent : Brushes.White, Foreground = primary ? Brushes.White : Foreground,
                BorderBrush = primary ? Accent : Border, BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            button.Style = ButtonStyle(primary);
            AutomationProperties.SetName(button, label);
            return button;
        }

        public static Button ActionButton(string label, string glyph, bool primary)
        {
            Button button = Button(label, primary);
            StackPanel content = new StackPanel { Orientation = Orientation.Horizontal };
            TextBlock symbol = Text(glyph, 13, primary ? Brushes.White : Accent);
            symbol.FontFamily = new FontFamily("Segoe MDL2 Assets");
            symbol.Margin = new Thickness(0, 0, 7, 0);
            content.Children.Add(symbol);
            content.Children.Add(Text(label, 12, primary ? Brushes.White : Foreground));
            button.Content = content;
            return button;
        }

        public static Style ButtonStyle(bool primary)
        {
            Style style = new Style(typeof(Button));
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            border.Name = "ButtonBorder";
            border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
            border.AppendChild(presenter);
            template.VisualTree = border;
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, primary ? Brush("#345E78") : Surface, "ButtonBorder"));
            template.Triggers.Add(hover);
            Trigger pressed = new Trigger { Property = System.Windows.Controls.Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, primary ? Brush("#18384D") : Brush("#DCE8EF"), "ButtonBorder"));
            template.Triggers.Add(pressed);
            Trigger focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, Accent, "ButtonBorder"));
            template.Triggers.Add(focus);
            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            template.Triggers.Add(disabled);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            Style focusVisual = new Style(typeof(Control));
            ControlTemplate focusTemplate = new ControlTemplate(typeof(Control));
            FrameworkElementFactory ring = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
            ring.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Accent);
            ring.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.5);
            ring.SetValue(System.Windows.Shapes.Shape.StrokeDashArrayProperty, new DoubleCollection(new double[] { 2, 2 }));
            ring.SetValue(FrameworkElement.MarginProperty, new Thickness(-3));
            ring.SetValue(UIElement.IsHitTestVisibleProperty, false);
            focusTemplate.VisualTree = ring;
            focusVisual.Setters.Add(new Setter(Control.TemplateProperty, focusTemplate));
            style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, focusVisual));
            return style;
        }
    }
}
