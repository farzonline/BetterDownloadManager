using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BDM.App.Views;

/// <summary>
/// Every button in the app answers a click: it sinks a little while held and springs back on release.
/// Registered once for the Button class, so new windows and styles get it without touching their XAML.
/// </summary>
public static class PressFeedback
{
    public const double Pressed = 0.94;
    static readonly IEasingFunction Spring = (BackEase)new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }.GetAsFrozen();

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Button), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((s, _) => Press((Button)s)), true);
        EventManager.RegisterClassHandler(typeof(Button), UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((s, _) => Release((Button)s)), true);
        EventManager.RegisterClassHandler(typeof(Button), UIElement.MouseLeaveEvent, new MouseEventHandler((s, _) => Release((Button)s)), true);
    }

    public static void Press(Button b)
    {
        if (b.IsEnabled) Animate(b, Pressed, 70, null);
    }

    public static void Release(Button b)
    {
        if (b.RenderTransform is ScaleTransform { ScaleX: < 1 }) Animate(b, 1, 280, Spring);
    }

    static void Animate(Button b, double to, int ms, IEasingFunction? ease)
    {
        if (b.RenderTransform is not ScaleTransform { IsFrozen: false } scale)
        {
            b.RenderTransformOrigin = new Point(0.5, 0.5);
            b.RenderTransform = scale = new ScaleTransform();
        }
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }
}
