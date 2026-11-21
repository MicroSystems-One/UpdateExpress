using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using UpdateExpress.Services;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;

namespace UpdateExpress;

/// <summary>
/// Update Express–styled replacement for classic Win32 MessageBox chrome:
/// soft page background, white rounded card, flat primary/secondary buttons, app language captions.
/// </summary>
internal sealed class AppDialog : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;

    private AppDialog(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image,
        Window? owner,
        string? primaryLabel = null,
        string? secondaryLabel = null,
        string? cancelLabel = null)
    {
        Title = title;
        Width = 500;
        SizeToContent = SizeToContent.Height;
        MinHeight = 170;
        MinWidth = 420;
        MaxWidth = 580;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        if (owner is not null) Owner = owner;
        Background = Hex("#F3F6FB");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        Icon = owner?.Icon;

        var primary = CreateChromeButton(
            primaryLabel ?? PrimaryCaption(buttons),
            primary: true,
            isDefault: true);
        primary.Click += (_, _) => Finish(PrimaryResult(buttons));

        var secondary = CreateChromeButton(
            secondaryLabel ?? SecondaryCaption(buttons),
            primary: false,
            isCancel: buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel or MessageBoxButton.OKCancel);
        secondary.Click += (_, _) => Finish(SecondaryResult(buttons));

        var buttonRow = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };

        if (buttons == MessageBoxButton.YesNoCancel)
        {
            var cancel = CreateChromeButton(cancelLabel ?? AppLanguage.Cancel, primary: false, isCancel: true);
            cancel.Click += (_, _) => Finish(MessageBoxResult.Cancel);
            buttonRow.Children.Add(cancel);
            buttonRow.Children.Add(secondary);
            buttonRow.Children.Add(primary);
        }
        else if (buttons == MessageBoxButton.OK)
        {
            buttonRow.Children.Add(primary);
        }
        else
        {
            buttonRow.Children.Add(secondary);
            buttonRow.Children.Add(primary);
        }

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = KindBadge(image);
        Grid.SetColumn(badge, 0);
        body.Children.Add(badge);
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 20,
            Foreground = Hex("#152137"),
            VerticalAlignment = WpfVerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0)
        };
        Grid.SetColumn(text, 1);
        body.Children.Add(text);

        Content = new Border
        {
            Margin = new Thickness(16),
            Background = Brushes.White,
            BorderBrush = Hex("#DFE5EC"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 15,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Hex("#152137"),
                        Margin = new Thickness(0, 0, 0, 12),
                        TextWrapping = TextWrapping.Wrap
                    },
                    body,
                    buttonRow
                }
            }
        };
    }

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        string? primaryLabel = null,
        string? secondaryLabel = null,
        string? cancelLabel = null)
    {
        var dialog = new AppDialog(message, title, buttons, image, owner, primaryLabel, secondaryLabel, cancelLabel);
        dialog.ShowDialog();
        return dialog._result == MessageBoxResult.None
            ? buttons switch
            {
                MessageBoxButton.YesNo => MessageBoxResult.No,
                MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
                MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
                _ => MessageBoxResult.OK
            }
            : dialog._result;
    }

    public static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        string? primaryLabel = null,
        string? secondaryLabel = null,
        string? cancelLabel = null) =>
        Show(System.Windows.Application.Current?.MainWindow as Window, message, title, buttons, image, primaryLabel, secondaryLabel, cancelLabel);

    /// <summary>Flat rounded chrome button used by AppDialog and AutoCloseInfoDialog.</summary>
    internal static Button CreateChromeButton(string content, bool primary, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button
        {
            Content = content,
            MinWidth = primary ? 96 : 92,
            MinHeight = 36,
            Height = 36,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(16, 6, 16, 6),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Background = primary ? Hex("#FF7900") : Hex("#EAF0F7"),
            Foreground = primary ? Brushes.White : Hex("#152137"),
            BorderBrush = primary ? Hex("#F06A00") : Hex("#DDE5EF"),
            BorderThickness = new Thickness(1),
            IsDefault = isDefault,
            IsCancel = isCancel,
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalContentAlignment = WpfVerticalAlignment.Center,
            HorizontalContentAlignment = WpfHorizontalAlignment.Center,
            Style = FlatButtonStyle()
        };
        return button;
    }

    private void Finish(MessageBoxResult result)
    {
        _result = result;
        try { DialogResult = result is MessageBoxResult.Yes or MessageBoxResult.OK; }
        catch { /* dialog may already be closing */ }
        Close();
    }

    private static string PrimaryCaption(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel => AppLanguage.Yes,
        _ => AppLanguage.Ok
    };

    private static string SecondaryCaption(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OKCancel => AppLanguage.Cancel,
        _ => AppLanguage.No
    };

    private static MessageBoxResult PrimaryResult(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel => MessageBoxResult.Yes,
        MessageBoxButton.OKCancel => MessageBoxResult.OK,
        _ => MessageBoxResult.OK
    };

    private static MessageBoxResult SecondaryResult(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel => MessageBoxResult.No,
        MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
        _ => MessageBoxResult.OK
    };

    private static Style FlatButtonStyle()
    {
        var style = new Style(typeof(Button));
        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        borderFactory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
            Path = new PropertyPath(BackgroundProperty)
        });
        borderFactory.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
            Path = new PropertyPath(BorderBrushProperty)
        });
        borderFactory.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
            Path = new PropertyPath(BorderThicknessProperty)
        });
        borderFactory.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
            Path = new PropertyPath(PaddingProperty)
        });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, WpfHorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, WpfVerticalAlignment.Center);
        borderFactory.AppendChild(presenter);
        template.VisualTree = borderFactory;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.9));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.82));
        template.Triggers.Add(pressed);

        style.Setters.Add(new Setter(TemplateProperty, template));
        return style;
    }

    private static UIElement KindBadge(MessageBoxImage image)
    {
        var (fill, glyph) = image switch
        {
            MessageBoxImage.Warning or MessageBoxImage.Exclamation => ("#F59E0B", "!"),
            MessageBoxImage.Error or MessageBoxImage.Hand or MessageBoxImage.Stop => ("#C62828", "!"),
            MessageBoxImage.Question => ("#1B4F86", "?"),
            _ => ("#FF7900", "i")
        };

        return new Grid
        {
            Width = 36,
            Height = 36,
            VerticalAlignment = WpfVerticalAlignment.Top,
            Children =
            {
                new Ellipse { Width = 36, Height = 36, Fill = Hex(fill) },
                new TextBlock
                {
                    Text = glyph,
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = WpfHorizontalAlignment.Center,
                    VerticalAlignment = WpfVerticalAlignment.Center
                }
            }
        };
    }

    private static SolidColorBrush Hex(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
}
