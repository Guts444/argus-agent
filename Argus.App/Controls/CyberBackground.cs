using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Numerics;

namespace Argus.App.Controls;

/// <summary>
/// Animated deep-space background for the dashboard surface.
/// </summary>
public sealed class CyberBackground : UserControl
{
    private CanvasAnimatedControl? _canvas;
    private readonly Random _rng = new();
    private readonly List<Particle> _particles = new();
    private float _time;
    private const int ParticleCount = 180;

    public CyberBackground()
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

        _particles.Clear();
        for (int i = 0; i < ParticleCount; i++)
        {
            _particles.Add(new Particle
            {
                X = (float)_rng.NextDouble(),
                Y = (float)_rng.NextDouble(),
                Speed = 0.03f + (float)_rng.NextDouble() * 0.12f,
                Drift = -0.02f + (float)_rng.NextDouble() * 0.04f,
                Size = 0.6f + (float)_rng.NextDouble() * 2.1f,
                Opacity = 0.35f + (float)_rng.NextDouble() * 0.65f,
                Phase = (float)_rng.NextDouble() * MathF.Tau
            });
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_canvas != null)
        {
            _canvas.Draw -= OnDraw;
            _canvas.Update -= OnUpdate;
            _canvas.RemoveFromVisualTree();
            _canvas = null;
        }
    }

    private void OnUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        var dt = (float)args.Timing.ElapsedTime.TotalSeconds;
        _time += dt;

        foreach (var p in _particles)
        {
            p.Y -= p.Speed * dt * 0.18f;
            p.X += p.Drift * dt * 0.08f;
            if (p.Y < -0.02f)
            {
                p.Y = 1.02f;
                p.X = (float)_rng.NextDouble();
                p.Speed = 0.03f + (float)_rng.NextDouble() * 0.12f;
            }

            if (p.X < -0.02f)
            {
                p.X = 1.02f;
            }
            else if (p.X > 1.02f)
            {
                p.X = -0.02f;
            }
        }
    }

    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var w = (float)sender.Size.Width;
        var h = (float)sender.Size.Height;

        ds.FillRectangle(0, 0, w, h, Windows.UI.Color.FromArgb(255, 0, 2, 8));

        foreach (var p in _particles)
        {
            var px = p.X * w;
            var py = p.Y * h;
            var twinkle = 0.55f + 0.45f * MathF.Sin(_time * 1.8f + p.Phase);
            var alpha = (byte)Math.Clamp(p.Opacity * twinkle * 255f, 35f, 245f);
            var warm = p.Size > 2.2f;
            var color = warm
                ? Windows.UI.Color.FromArgb(alpha, 226, 238, 255)
                : Windows.UI.Color.FromArgb(alpha, 245, 250, 255);
            ds.FillCircle(px, py, p.Size, color);

            if (p.Size > 1.8f)
            {
                ds.FillCircle(px, py, p.Size * 3.4f, Windows.UI.Color.FromArgb((byte)(alpha / 5), 70, 180, 255));
            }
        }

        // Faint orbital lane keeps the HUD tied to the graph theme without washing the space field grey.
        var streamY = (float)(Math.Sin(_time * 0.7) * 0.4 + 0.5) * h;
        var streamAlpha = (byte)(6 + Math.Abs(Math.Sin(_time * 2.0)) * 12);
        ds.DrawLine(0, streamY, w, streamY,
            Windows.UI.Color.FromArgb(streamAlpha, 0, 200, 255), 1.5f);

        // Corner brackets
        float cornerLen = 30f;
        float cm = 16f;
        var accent = Windows.UI.Color.FromArgb(25, 0, 240, 255);
        ds.DrawLine(cm, cm, cm + cornerLen, cm, accent, 1f);
        ds.DrawLine(cm, cm, cm, cm + cornerLen, accent, 1f);
        ds.DrawLine(w - cm, cm, w - cm - cornerLen, cm, accent, 1f);
        ds.DrawLine(w - cm, cm, w - cm, cm + cornerLen, accent, 1f);
        ds.DrawLine(cm, h - cm, cm + cornerLen, h - cm, accent, 1f);
        ds.DrawLine(cm, h - cm, cm, h - cm - cornerLen, accent, 1f);
        ds.DrawLine(w - cm, h - cm, w - cm - cornerLen, h - cm, accent, 1f);
        ds.DrawLine(w - cm, h - cm, w - cm, h - cm - cornerLen, accent, 1f);
    }

    private class Particle
    {
        public float X, Y, Speed, Drift, Size, Opacity, Phase;
    }
}
