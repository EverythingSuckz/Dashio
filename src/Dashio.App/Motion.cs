using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Dashio.App;

/// <summary>Short, standard animations for things that would otherwise pop in or out.</summary>
public static class Motion
{
    private static readonly Duration In = TimeSpan.FromMilliseconds(220);
    private static readonly Duration Out = TimeSpan.FromMilliseconds(140);

    // The animation each element is running or holding, so a new one can take over cleanly.
    private static readonly ConditionalWeakTable<UIElement, Storyboard> Active = [];

    /// <summary>Fades the element in while it settles from a small offset, as page content does.</summary>
    public static void Enter(UIElement element, double fromY = 16)
    {
        var storyboard = Build(element, fromOpacity: 0, toOpacity: 1, "Y", fromY, to: 0, In, new CubicEase { EasingMode = EasingMode.EaseOut });
        // It ends where the element rests anyway, so nothing needs to be held afterwards.
        storyboard.FillBehavior = FillBehavior.Stop;
        storyboard.Begin();
    }

    /// <summary>
    /// Fades the element in while it slides in sideways: from the right for a positive offset,
    /// from the left for a negative one. For moving between tabs that sit side by side.
    /// </summary>
    public static void EnterSideways(UIElement element, double fromX)
    {
        var storyboard = Build(element, fromOpacity: 0, toOpacity: 1, "X", fromX, to: 0, In, new CubicEase { EasingMode = EasingMode.EaseOut });
        storyboard.FillBehavior = FillBehavior.Stop;
        storyboard.Begin();
    }

    /// <summary>Fades the element out, lifting slightly, and completes when it is gone.</summary>
    public static Task ExitAsync(UIElement element, double toY = -8)
    {
        var done = new TaskCompletionSource();
        var storyboard = Build(element, fromOpacity: 1, toOpacity: 0, "Y", from: 0, toY, Out, new CubicEase { EasingMode = EasingMode.EaseIn });
        storyboard.Completed += (_, _) => done.TrySetResult();
        storyboard.Begin();
        return done.Task;
    }

    /// <summary>Puts the element back to fully visible and in place.</summary>
    public static void Reset(UIElement element)
    {
        if (Active.TryGetValue(element, out var running))
        {
            running.Stop();
            Active.Remove(element);
        }
    }

    private static Storyboard Build(
        UIElement element, double fromOpacity, double toOpacity, string axis, double from, double to, Duration duration, EasingFunctionBase easing)
    {
        Reset(element);
        if (element.RenderTransform is not TranslateTransform)
            element.RenderTransform = new TranslateTransform();

        var opacity = new DoubleAnimation { From = fromOpacity, To = toOpacity, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(opacity, element);
        Storyboard.SetTargetProperty(opacity, "Opacity");

        var slide = new DoubleAnimation { From = from, To = to, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(slide, element.RenderTransform);
        Storyboard.SetTargetProperty(slide, axis);

        var storyboard = new Storyboard();
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(slide);
        Active.Add(element, storyboard);
        return storyboard;
    }
}
