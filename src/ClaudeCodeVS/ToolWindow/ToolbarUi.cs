using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS.ToolWindow
{
    /// <summary>
    /// Builds the flat, theme-aware buttons used by the tool window. Everything is created
    /// in code (no XAML) and coloured through Visual Studio theme resources, so the
    /// controls follow the light, dark and blue themes.
    /// </summary>
    internal static class ToolbarUi
    {
        // Icon glyphs from the Segoe icon fonts that ship with Windows 10 and 11.
        public const string GlyphNewChat = "\uE710";
        public const string GlyphHistory = "\uE81C";
        public const string GlyphDownload = "\uE896";
        public const string GlyphFixErrors = "\uE90F";
        public const string GlyphNewWindow = "\uE8A7";
        public const string GlyphClose = "\uE711";
        public const string GlyphRename = "\uE8AC";

        private static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        private static Style _buttonStyle;

        public static Button CreateToolbarButton(string glyph, string text, string toolTip)
        {
            var content = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = IconFont,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (!string.IsNullOrEmpty(text))
            {
                content.Children.Add(new TextBlock
                {
                    Text = text,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            return new Button
            {
                Content = content,
                Style = ButtonStyle,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(2, 0, 0, 0),
                ToolTip = toolTip
            };
        }

        public static Button CreateListItem(string title, string detail)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = title,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            content.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 11,
                Opacity = 0.65,
                Margin = new Thickness(0, 1, 0, 0)
            });

            return new Button
            {
                Content = content,
                Style = ButtonStyle,
                Padding = new Thickness(8, 5, 8, 5),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                ToolTip = title
            };
        }

        private static Style ButtonStyle
        {
            get
            {
                if (_buttonStyle == null)
                {
                    var style = new Style(typeof(Button));
                    style.Setters.Add(new Setter(Control.ForegroundProperty,
                        new DynamicResourceExtension(VsBrushes.ToolWindowTextKey)));
                    style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
                    style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
                    style.Setters.Add(new Setter(Control.TemplateProperty, CreateTemplate()));
                    _buttonStyle = style;
                }

                return _buttonStyle;
            }
        }

        private static ControlTemplate CreateTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border), "border");
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty,
                new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            border.AppendChild(presenter);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                new DynamicResourceExtension(VsBrushes.CommandBarHoverKey), "border"));
            hover.Setters.Add(new Setter(Control.ForegroundProperty,
                new DynamicResourceExtension(VsBrushes.CommandBarTextHoverKey)));
            template.Triggers.Add(hover);

            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.7, "border"));
            template.Triggers.Add(pressed);

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4, "border"));
            template.Triggers.Add(disabled);

            return template;
        }
    }
}
