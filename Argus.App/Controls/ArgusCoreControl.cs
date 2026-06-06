using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Numerics;

namespace Argus.App.Controls;

/// <summary>
/// Animated dotted core used as the dashboard focal point.
/// </summary>
public sealed class ArgusCoreControl : UserControl
{
    private readonly Random _rng = new(73);
    private readonly List<CoreDot> _dots = new();
    private CanvasAnimatedControl? _canvas;
    private float _time;

    public ArgusCoreControl()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _canvas = new CanvasAnimatedControl
        {
            ClearColor = Colors.Transparent,
            IsFixedTimeStep = false
        };
        _canvas.Draw += OnDraw;
        _canvas.Update += OnUpdate;
        Content = _canvas;

        BuildDots();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_canvas is null)
        {
            return;
        }

        _canvas.Draw -= OnDraw;
        _canvas.Update -= OnUpdate;
        _canvas.RemoveFromVisualTree();
        _canvas = null;
    }

    private void BuildDots()
    {
        _dots.Clear();

        for (var i = 0; i < 96; i++)
        {
            var y = -1f + 2f * (float)_rng.NextDouble();
            var width = 0.18f + 0.48f * MathF.Pow(MathF.Sin((y + 1f) * MathF.PI * 0.5f), 1.7f);
            var x = (-width + 2f * width * (float)_rng.NextDouble()) * (0.45f + 0.55f * (float)_rng.NextDouble());
            var colorRoll = _rng.NextDouble();

            // Scale down so it fits inside the inner ring (whose radius is 0.52)
            x *= 0.40f;
            y *= 0.40f;

            _dots.Add(new CoreDot(
                x,
                y,
                1.9f + (float)_rng.NextDouble() * 3.4f,
                (float)_rng.NextDouble() * MathF.Tau,
                colorRoll < 0.58 ? DotColor.Cyan : colorRoll < 0.84 ? DotColor.Magenta : DotColor.White));
        }

        for (var i = 0; i < 54; i++)
        {
            var angle = i / 54f * MathF.Tau;
            // Alternate between inner ring (0.52) and outer ring (0.65)
            var r = (i % 2 == 0) ? 0.52f : 0.65f;
            // Tiny wiggle for organic look
            r += 0.005f * MathF.Sin(i * 1.7f);

            _dots.Add(new CoreDot(
                MathF.Cos(angle) * r,
                MathF.Sin(angle) * r,
                1.3f + (i % 4) * 0.35f,
                angle,
                i % 3 == 0 ? DotColor.Magenta : DotColor.Cyan,
                true));
        }
    }

    private void OnUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        _time += (float)args.Timing.ElapsedTime.TotalSeconds;
    }

    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var w = (float)sender.Size.Width;
        var h = (float)sender.Size.Height;
        var center = new Vector2(w / 2f, h / 2f);
        var radius = MathF.Min(w, h) * 0.42f;
        var pulse = 0.5f + 0.5f * MathF.Sin(_time * 1.8f);

        // Draw exactly two concentric rings
        var innerRingRadius = radius * 0.52f;
        var outerRingRadius = radius * 0.65f;

        // Faint glowing fills for depth
        ds.FillCircle(center, innerRingRadius, Windows.UI.Color.FromArgb(12, 255, 79, 216)); // Faint magenta
        ds.FillCircle(center, outerRingRadius, Windows.UI.Color.FromArgb(10, 0, 240, 255)); // Faint cyan

        // Outlines
        ds.DrawCircle(center, innerRingRadius, Windows.UI.Color.FromArgb(150, 255, 79, 216), 1.1f); // Inner magenta ring
        ds.DrawCircle(center, outerRingRadius, Windows.UI.Color.FromArgb(150, 0, 240, 255), 1.2f); // Outer cyan ring

        foreach (var dot in _dots)
        {
            var orbitOffset = dot.Orbit ? _time * 0.18f : 0f;
            var x = dot.X;
            var y = dot.Y;
            if (dot.Orbit)
            {
                var angle = dot.Phase + orbitOffset;
                var r = MathF.Sqrt(dot.X * dot.X + dot.Y * dot.Y);
                x = MathF.Cos(angle) * r;
                y = MathF.Sin(angle) * r;
            }

            var shimmer = 0.55f + 0.45f * MathF.Sin(_time * 2.6f + dot.Phase);
            var position = center + new Vector2(x * radius, y * radius);
            var size = dot.Size * (0.82f + shimmer * 0.28f);
            var color = ToColor(dot.Color, (byte)(95 + shimmer * 130));
            var glow = ToColor(dot.Color, (byte)(16 + shimmer * 35));

            ds.FillCircle(position, size * 3.6f, glow);
            ds.FillCircle(position, size, color);
        }
    }

    private static Windows.UI.Color ToColor(DotColor color, byte alpha)
    {
        return color switch
        {
            DotColor.Magenta => Windows.UI.Color.FromArgb(alpha, 255, 79, 216),
            DotColor.White => Windows.UI.Color.FromArgb(alpha, 245, 250, 255),
            _ => Windows.UI.Color.FromArgb(alpha, 54, 247, 255)
        };
    }

    private sealed record CoreDot(float X, float Y, float Size, float Phase, DotColor Color, bool Orbit = false);
    private enum DotColor { Cyan, Magenta, White }
}
