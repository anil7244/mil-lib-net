using System;
using System.Threading;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Styling;
using MilLib.Desktop.ViewModels;

namespace MilLib.Desktop.Views;

public partial class DashboardView : UserControl
{
    private DashboardViewModel? _model;
    private CancellationTokenSource? _marquee;
    private double _period;

    public DashboardView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Hook();

        // The set's width is only known once its covers have been laid out, and
        // it changes when the dashboard is refreshed — so the scroll is started
        // (and restarted) from here rather than once at load.
        if (this.FindControl<ItemsControl>("MarqueeSetA") is { } set)
        {
            set.LayoutUpdated += (_, _) => StartMarquee();
        }

        Hook();
    }

    private void Hook()
    {
        if (_model is not null)
        {
            _model.CoversChanged -= OnCoversChanged;
        }

        _model = DataContext as DashboardViewModel;

        if (_model is not null)
        {
            _model.CoversChanged += OnCoversChanged;
        }
    }

    private void OnCoversChanged()
    {
        // Force a fresh measure on the next pass, so a reload restarts the scroll.
        _period = 0;
    }

    /// <summary>
    /// Scroll the strip of covers right to left, forever and seamlessly: the
    /// covers are laid out twice, and the strip is moved left by exactly one
    /// set — so when the loop snaps back, the second copy is already where the
    /// first began and nothing jumps.
    /// </summary>
    private void StartMarquee()
    {
        if (this.FindControl<ItemsControl>("MarqueeSetA") is not { } set
            || this.FindControl<StackPanel>("MarqueeStrip") is not { } strip)
        {
            return;
        }

        var width = set.Bounds.Width;

        if (width <= 1)
        {
            return; // not laid out yet
        }

        // One set, plus the gap to its copy — the distance that makes the loop
        // invisible. If it has not changed, the scroll is already running.
        var period = width + 14;

        if (Math.Abs(period - _period) < 2)
        {
            return;
        }

        _period = period;

        _marquee?.Cancel();
        _marquee = new CancellationTokenSource();

        const double pixelsPerSecond = 42;

        var animation = new Animation
        {
            Duration = TimeSpan.FromSeconds(Math.Max(8, period / pixelsPerSecond)),
            IterationCount = IterationCount.Infinite,
            Easing = new LinearEasing(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Canvas.LeftProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Canvas.LeftProperty, -period) } },
            },
        };

        _ = animation.RunAsync(strip, _marquee.Token);
    }
}
