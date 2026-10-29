using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RigUninstaller.Helpers;

/// <summary>
/// Small entrance animations, set in XAML: <c>helpers:Motion.FadeIn="True"</c> fades an element in whenever it appears;
/// <c>helpers:Motion.RiseIn="True"</c> also lifts it 8 px into place. Both do nothing when Windows animations are off.
/// </summary>
public static class Motion
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(180);
    private const double RiseDistance = 8;

    public static readonly DependencyProperty FadeInProperty = DependencyProperty.RegisterAttached(
        "FadeIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) => Hook(d, e, rise: false)));

    public static readonly DependencyProperty RiseInProperty = DependencyProperty.RegisterAttached(
        "RiseIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) => Hook(d, e, rise: true)));

    public static bool GetFadeIn(DependencyObject element) => (bool)element.GetValue(FadeInProperty);

    public static void SetFadeIn(DependencyObject element, bool value) => element.SetValue(FadeInProperty, value);

    public static bool GetRiseIn(DependencyObject element) => (bool)element.GetValue(RiseInProperty);

    public static void SetRiseIn(DependencyObject element, bool value) => element.SetValue(RiseInProperty, value);

    private static void Hook(DependencyObject sender, DependencyPropertyChangedEventArgs e, bool rise)
    {
        if (sender is not FrameworkElement element || e.NewValue is not true)
        {
            return;
        }

        // A tab's content is put back into the page each time the tab is opened; a hidden panel is shown again later.
        element.Loaded += (_, _) => Play(element, rise);
        element.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true && element.IsLoaded)
            {
                Play(element, rise);
            }
        };
    }

    private static void Play(FrameworkElement element, bool rise)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };

        // FillBehavior.Stop hands the property back to its normal value (1 and 0) when the animation ends.
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });

        if (!rise)
        {
            return;
        }

        if (element.RenderTransform is not TranslateTransform { IsFrozen: false } transform)
        {
            transform = new TranslateTransform();
            element.RenderTransform = transform;
        }

        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(RiseDistance, 0, Duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }
}
