using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClaudeTaskBoard;

// Animated replacement for Visibility bindings: local:Anim.Show="{Binding X}".
// Showing fades in while growing from zero height; hiding plays the reverse and only then collapses,
// so the surrounding layout (and the auto-sized window) slides instead of jumping.
// Elements that start hidden should also set Visibility="Collapsed" in XAML.
public static class Anim
{
    public static readonly DependencyProperty ShowProperty = DependencyProperty.RegisterAttached(
        "Show", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnShowChanged));

    public static bool GetShow(DependencyObject d) => (bool)d.GetValue(ShowProperty);
    public static void SetShow(DependencyObject d, bool value) => d.SetValue(ShowProperty, value);

    static readonly Duration InTime = new(TimeSpan.FromMilliseconds(200));
    static readonly Duration OutTime = new(TimeSpan.FromMilliseconds(150));
    public static readonly TimeSpan OutDuration = OutTime.TimeSpan;

    static void OnShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        bool show = (bool)e.NewValue;

        if (el.LayoutTransform is not ScaleTransform scale || scale.IsFrozen)
            el.LayoutTransform = scale = new ScaleTransform(1, 1);

        // Respect "Animation effects: off" in Windows settings.
        if (!SystemParameters.ClientAreaAnimation)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            el.Opacity = 1;
            scale.ScaleY = 1;
            el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (show)
        {
            // From a collapsed state start at zero; mid-exit, reverse from wherever it is now.
            bool wasHidden = el.Visibility != Visibility.Visible;
            el.Visibility = Visibility.Visible;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            el.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation { From = wasHidden ? 0 : null, To = 1, Duration = InTime, EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation { From = wasHidden ? 0 : null, To = 1, Duration = InTime, EasingFunction = ease });
        }
        else
        {
            if (el.Visibility != Visibility.Visible) return;
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation { To = 0, Duration = OutTime, EasingFunction = ease };
            fade.Completed += (_, _) => { if (!GetShow(el)) el.Visibility = Visibility.Collapsed; };
            el.BeginAnimation(UIElement.OpacityProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation { To = 0, Duration = OutTime, EasingFunction = ease });
        }
    }
}
