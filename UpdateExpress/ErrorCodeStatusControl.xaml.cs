using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace UpdateExpress;

public partial class ErrorCodeStatusControl : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ErrorCodeProperty =
        DependencyProperty.Register(nameof(ErrorCode), typeof(string), typeof(ErrorCodeStatusControl),
            new PropertyMetadata("", OnErrorCodeChanged));

    public static readonly RoutedEvent FixRequestedEvent =
        EventManager.RegisterRoutedEvent(nameof(FixRequested), RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(ErrorCodeStatusControl));

    private readonly DispatcherTimer _timer;
    private bool _showFixIt;
    private DoubleAnimation? _pulse;

    public ErrorCodeStatusControl()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _timer.Tick += (_, _) =>
        {
            _showFixIt = !_showFixIt;
            ApplyText();
            Pulse();
        };
        Loaded += (_, _) =>
        {
            ApplyText();
            if (!_timer.IsEnabled) _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    public string ErrorCode
    {
        get => (string)GetValue(ErrorCodeProperty);
        set => SetValue(ErrorCodeProperty, value);
    }

    public event RoutedEventHandler FixRequested
    {
        add => AddHandler(FixRequestedEvent, value);
        remove => RemoveHandler(FixRequestedEvent, value);
    }

    private static void OnErrorCodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ErrorCodeStatusControl c)
            c.ApplyText();
    }

    private void ApplyText()
    {
        string code = ErrorCode ?? "";
        Label.Text = _showFixIt ? "Fix it" : (string.IsNullOrWhiteSpace(code) ? "Fix it" : code);
    }

    private void Pulse()
    {
        _pulse ??= new DoubleAnimation
        {
            From = 0.45,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(700),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Label.BeginAnimation(OpacityProperty, _pulse);
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(FixRequestedEvent, this));
        e.Handled = true;
    }
}
