using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using Argus.Core.Models;
using Argus.App.ViewModels;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Argus.App.Controls;

public sealed class GraphCanvasControl : Grid
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(MainPageViewModel), typeof(GraphCanvasControl), new PropertyMetadata(null, OnViewModelChanged));

    private readonly CanvasTextFormat labelFormat = new() { FontFamily = "Segoe UI Variable", FontSize = 13 };
    private readonly CanvasTextFormat metaFormat = new() { FontFamily = "Segoe UI Variable", FontSize = 11 };
    private readonly CanvasControl canvas = new();
    private readonly Stopwatch animationClock = new();
    private double zoom = 1;
    private double panX = 40;
    private double panY = 20;
    private Point lastPoint;
    private Node? draggedNode;
    private Node? hoveredNode;
    private Node? connectSourceNode;
    private Point connectPreviewPoint;
    private Rect minimapRect;
    private bool isPanning;
    private bool isMinimapPanning;
    private bool subscriptionsAttached;
    private bool renderingAttached;

    public GraphCanvasControl()
    {
        canvas.ClearColor = Color.FromArgb(0, 0, 0, 0);
        canvas.Draw += OnDraw;
        canvas.PointerPressed += OnPointerPressed;
        canvas.PointerMoved += OnPointerMoved;
        canvas.PointerReleased += OnPointerReleased;
        canvas.PointerWheelChanged += OnPointerWheelChanged;
        canvas.DoubleTapped += OnDoubleTapped;
        Children.Add(canvas);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public MainPageViewModel? ViewModel
    {
        get => (MainPageViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public void FitToView()
    {
        if (ViewModel is null || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0)
        {
            return;
        }

        var nodes = GetVisibleNodes(ViewModel);
        if (nodes.Count == 0)
        {
            return;
        }

        var minX = nodes.Min(node => node.PositionX ?? 0);
        var maxX = nodes.Max(node => node.PositionX ?? 0);
        var minY = nodes.Min(node => node.PositionY ?? 0);
        var maxY = nodes.Max(node => node.PositionY ?? 0);
        var width = Math.Max(maxX - minX, 1);
        var height = Math.Max(maxY - minY, 1);
        zoom = Math.Clamp(Math.Min((canvas.ActualWidth - 100) / width, (canvas.ActualHeight - 100) / height), 0.35, 2.4);
        panX = canvas.ActualWidth / 2 - (minX + width / 2) * zoom;
        panY = canvas.ActualHeight / 2 - (minY + height / 2) * zoom;
        InvalidateCanvas();
    }

    public void ResetView()
    {
        zoom = 1;
        panX = 40;
        panY = 20;
        InvalidateCanvas();
    }

    public async Task ClusterByTypeAsync()
    {
        var viewModel = ViewModel;
        if (viewModel is null || viewModel.Nodes.Count == 0)
        {
            return;
        }

        var groups = viewModel.Nodes
            .GroupBy(node => node.Type)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .ToList();

        const double centerX = 560;
        const double centerY = 360;
        const double clusterRadius = 310;

        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex].ToList();
            var clusterAngle = Math.PI * 2 * groupIndex / Math.Max(groups.Count, 1) - Math.PI / 2;
            var clusterCenterX = centerX + Math.Cos(clusterAngle) * clusterRadius;
            var clusterCenterY = centerY + Math.Sin(clusterAngle) * clusterRadius;
            var nodeRadius = Math.Max(54, 26 + group.Count * 9);

            for (var nodeIndex = 0; nodeIndex < group.Count; nodeIndex++)
            {
                var node = group[nodeIndex];
                if (viewModel.IsNodePinned(node.Id))
                {
                    continue;
                }
                var nodeAngle = Math.PI * 2 * nodeIndex / Math.Max(group.Count, 1);
                node.PositionX = clusterCenterX + Math.Cos(nodeAngle) * nodeRadius;
                node.PositionY = clusterCenterY + Math.Sin(nodeAngle) * nodeRadius;
            }
        }

        await viewModel.PersistNodePositionsAsync(viewModel.Nodes);
        FitToView();
    }

    private static void OnViewModelChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (GraphCanvasControl)dependencyObject;
        control.Detach(args.OldValue as MainPageViewModel);
        control.Attach(args.NewValue as MainPageViewModel);
        control.InvalidateCanvas();
    }

    private void Attach(MainPageViewModel? viewModel)
    {
        if (viewModel is null || subscriptionsAttached)
        {
            return;
        }

        viewModel.Nodes.CollectionChanged += OnCollectionChanged;
        viewModel.Edges.CollectionChanged += OnCollectionChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        subscriptionsAttached = true;
    }

    private void Detach(MainPageViewModel? viewModel)
    {
        if (viewModel is null || !subscriptionsAttached)
        {
            return;
        }

        viewModel.Nodes.CollectionChanged -= OnCollectionChanged;
        viewModel.Edges.CollectionChanged -= OnCollectionChanged;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        subscriptionsAttached = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Attach(ViewModel);
        animationClock.Restart();
        if (!renderingAttached)
        {
            CompositionTarget.Rendering += OnRendering;
            renderingAttached = true;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (renderingAttached)
        {
            CompositionTarget.Rendering -= OnRendering;
            renderingAttached = false;
        }
        animationClock.Stop();
        draggedNode = null;
        hoveredNode = null;
        connectSourceNode = null;
        isPanning = false;
        isMinimapPanning = false;
        Detach(ViewModel);
    }

    private void OnRendering(object? sender, object e)
    {
        if (IsLoaded &&
            Visibility == Visibility.Visible &&
            canvas.IsLoaded &&
            ViewModel?.Nodes.Count > 0)
        {
            InvalidateCanvas();
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateCanvas();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainPageViewModel.SelectedNode) or nameof(MainPageViewModel.GraphFilterType))
        {
            InvalidateCanvas();
        }
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        var ds = args.DrawingSession;
        DrawBackground(ds);

        var visibleNodes = GetVisibleNodes(viewModel);
        var visibleEdges = GetVisibleEdges(viewModel, visibleNodes);
        var nodes = visibleNodes.ToDictionary(node => node.Id);
        var degrees = visibleEdges
            .SelectMany(edge => new[] { edge.SourceNodeId, edge.TargetNodeId })
            .GroupBy(id => id)
            .ToDictionary(group => group.Key, group => group.Count());

        foreach (var edge in visibleEdges)
        {
            if (!nodes.TryGetValue(edge.SourceNodeId, out var source) || !nodes.TryGetValue(edge.TargetNodeId, out var target))
            {
                continue;
            }

            var sourcePoint = ToScreen(source.PositionX ?? 0, source.PositionY ?? 0);
            var targetPoint = ToScreen(target.PositionX ?? 0, target.PositionY ?? 0);
            var color = Color.FromArgb((byte)(70 + edge.Strength * 115), 54, 247, 255);
            var width = (float)(1.2 + edge.Strength * 2.4);
            ds.DrawLine(sourcePoint, targetPoint, Color.FromArgb(28, color.R, color.G, color.B), width + 8);
            ds.DrawLine(sourcePoint, targetPoint, Color.FromArgb(58, color.R, color.G, color.B), width + 4);
            ds.DrawLine(sourcePoint, targetPoint, color, width);
            DrawEdgePulse(ds, sourcePoint, targetPoint, color, edge.Strength);
        }

        foreach (var node in visibleNodes.OrderBy(node => node.Id == viewModel.SelectedNode?.Id ? 1 : 0))
        {
            var point = ToScreen(node.PositionX ?? 0, node.PositionY ?? 0);
            var degree = degrees.GetValueOrDefault(node.Id);
            var radius = NodeRadius(node, degree);
            var color = ColorFor(node.ColorKey);
            var selected = node.Id == viewModel.SelectedNode?.Id;
            var hovered = node.Id == hoveredNode?.Id;
            var pinned = viewModel.IsNodePinned(node.Id);
            var pulse = selected ? 1.0 + Math.Sin(animationClock.Elapsed.TotalSeconds * 4.2) * 0.16 : 1.0;

            ds.FillCircle(point.X, point.Y, (float)((radius + 26) * pulse), Color.FromArgb(selected ? (byte)58 : hovered ? (byte)42 : (byte)22, color.R, color.G, color.B));
            ds.FillCircle(point.X, point.Y, radius + 14, Color.FromArgb(selected ? (byte)70 : (byte)34, color.R, color.G, color.B));
            ds.FillCircle(point.X, point.Y, radius + 6, Color.FromArgb(52, color.R, color.G, color.B));
            ds.FillCircle(point.X, point.Y, radius, Color.FromArgb(238, color.R, color.G, color.B));

            if (pinned)
            {
                ds.DrawCircle(point.X, point.Y, radius + 4, Color.FromArgb(220, 255, 200, 87), 1.6f);
            }

            ds.DrawCircle(point.X, point.Y, radius + (selected ? 8 : hovered ? 5 : 2), Color.FromArgb(230, 234, 248, 255), selected ? 2.4f : hovered ? 1.8f : 1.1f);
            ds.DrawText(node.Title, new Rect(point.X + radius + 8, point.Y - 15, 170, 24), Color.FromArgb(235, 234, 248, 255), labelFormat);
            ds.DrawText(node.Type, new Rect(point.X + radius + 8, point.Y + 4, 130, 20), Color.FromArgb(170, 168, 183, 196), metaFormat);
        }

        if (connectSourceNode is not null)
        {
            var sourcePoint = ToScreen(connectSourceNode.PositionX ?? 0, connectSourceNode.PositionY ?? 0);
            ds.DrawLine(sourcePoint, new Vector2((float)connectPreviewPoint.X, (float)connectPreviewPoint.Y), Color.FromArgb(190, 255, 200, 87), 2.2f);
            ds.FillCircle((float)connectPreviewPoint.X, (float)connectPreviewPoint.Y, 5, Color.FromArgb(220, 255, 200, 87));
        }

        DrawMinimap(ds, visibleNodes, visibleEdges);
    }

    private void DrawBackground(CanvasDrawingSession ds)
    {
        var width = (float)Math.Max(canvas.ActualWidth, 1);
        var height = (float)Math.Max(canvas.ActualHeight, 1);
        ds.FillRectangle(0, 0, width, height, Color.FromArgb(255, 6, 9, 16));

        const float spacing = 44;
        var gridColor = Color.FromArgb(26, 54, 247, 255);
        for (float x = (float)(panX % spacing); x < width; x += spacing)
        {
            ds.DrawLine(x, 0, x, height, gridColor, 1);
        }

        for (float y = (float)(panY % spacing); y < height; y += spacing)
        {
            ds.DrawLine(0, y, width, y, gridColor, 1);
        }

        var sweep = (float)((animationClock.Elapsed.TotalSeconds * 26) % Math.Max(width, 1));
        ds.DrawLine(sweep, 0, sweep - 120, height, Color.FromArgb(24, 54, 247, 255), 2);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        canvas.CapturePointer(e.Pointer);
        lastPoint = e.GetCurrentPoint(canvas).Position;
        if (minimapRect.Contains(lastPoint))
        {
            isMinimapPanning = true;
            CenterViewportFromMinimap(lastPoint);
            e.Handled = true;
            return;
        }

        var world = ToWorld(lastPoint);
        draggedNode = HitTest(world);
        if (draggedNode is not null && e.GetCurrentPoint(canvas).Properties.IsRightButtonPressed)
        {
            connectSourceNode = draggedNode;
            connectPreviewPoint = lastPoint;
            draggedNode = null;
            isPanning = false;
            e.Handled = true;
            InvalidateCanvas();
            return;
        }

        if (ViewModel is not null)
        {
            ViewModel.SelectedNode = draggedNode;
        }

        isPanning = draggedNode is null;
        e.Handled = true;
        InvalidateCanvas();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(canvas).Position;
        if (isMinimapPanning)
        {
            CenterViewportFromMinimap(point);
            lastPoint = point;
            return;
        }

        if (connectSourceNode is not null)
        {
            connectPreviewPoint = point;
            hoveredNode = HitTest(ToWorld(point));
            InvalidateCanvas();
            lastPoint = point;
            return;
        }

        if (draggedNode is not null && e.Pointer.PointerDeviceType is not Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            var world = ToWorld(point);
            draggedNode.PositionX = world.X;
            draggedNode.PositionY = world.Y;
            InvalidateCanvas();
        }
        else if (isPanning)
        {
            panX += point.X - lastPoint.X;
            panY += point.Y - lastPoint.Y;
            InvalidateCanvas();
        }
        else
        {
            hoveredNode = HitTest(ToWorld(point));
            InvalidateCanvas();
        }

        lastPoint = point;
    }

    private async void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        canvas.ReleasePointerCapture(e.Pointer);
        if (isMinimapPanning)
        {
            isMinimapPanning = false;
            return;
        }

        if (connectSourceNode is not null && ViewModel is not null)
        {
            var target = HitTest(ToWorld(e.GetCurrentPoint(canvas).Position));
            if (target is not null && target.Id != connectSourceNode.Id)
            {
                await ViewModel.CreateEdgeBetweenAsync(connectSourceNode, target);
            }

            connectSourceNode = null;
            hoveredNode = target;
            InvalidateCanvas();
            return;
        }

        if (draggedNode is not null && ViewModel is not null)
        {
            await ViewModel.SaveNodePositionAsync(draggedNode);
        }

        draggedNode = null;
        isPanning = false;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(canvas);
        var wheel = point.Properties.MouseWheelDelta;
        var before = ToWorld(point.Position);
        zoom = Math.Clamp(zoom * (wheel > 0 ? 1.12 : 0.88), 0.25, 3.2);
        panX = point.Position.X - before.X * zoom;
        panY = point.Position.Y - before.Y * zoom;
        InvalidateCanvas();
        e.Handled = true;
    }

    private async void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var world = ToWorld(e.GetPosition(canvas));
        if (HitTest(world) is null)
        {
            await ViewModel.CreateNodeAtAsync(world.X, world.Y);
        }
    }

    private Vector2 ToScreen(double x, double y)
    {
        return new Vector2((float)(x * zoom + panX), (float)(y * zoom + panY));
    }

    private Point ToWorld(Point screenPoint)
    {
        return new Point((screenPoint.X - panX) / zoom, (screenPoint.Y - panY) / zoom);
    }

    private Node? HitTest(Point worldPoint)
    {
        if (ViewModel is null)
        {
            return null;
        }

        var degrees = ViewModel.Edges
            .SelectMany(edge => new[] { edge.SourceNodeId, edge.TargetNodeId })
            .GroupBy(id => id)
            .ToDictionary(group => group.Key, group => group.Count());

        foreach (var node in GetVisibleNodes(ViewModel).Reverse())
        {
            var dx = (node.PositionX ?? 0) - worldPoint.X;
            var dy = (node.PositionY ?? 0) - worldPoint.Y;
            var radius = NodeRadius(node, degrees.GetValueOrDefault(node.Id)) / zoom + 12;
            if (Math.Sqrt(dx * dx + dy * dy) <= radius)
            {
                return node;
            }
        }

        return null;
    }

    private void DrawEdgePulse(CanvasDrawingSession ds, Vector2 sourcePoint, Vector2 targetPoint, Color color, double strength)
    {
        var phase = (animationClock.Elapsed.TotalSeconds * (0.22 + strength * 0.28)) % 1.0;
        var x = sourcePoint.X + (targetPoint.X - sourcePoint.X) * (float)phase;
        var y = sourcePoint.Y + (targetPoint.Y - sourcePoint.Y) * (float)phase;
        ds.FillCircle(x, y, (float)(2.4 + strength * 2.2), Color.FromArgb(190, color.R, color.G, color.B));
    }

    private void DrawMinimap(CanvasDrawingSession ds, IReadOnlyCollection<Node> nodes, IReadOnlyCollection<Edge> edges)
    {
        if (nodes.Count == 0)
        {
            return;
        }

        minimapRect = new Rect(Math.Max(canvas.ActualWidth - 214, 18), Math.Max(canvas.ActualHeight - 154, 18), 178, 118);
        var bounds = GetWorldBounds(nodes);
        var scale = Math.Min((minimapRect.Width - 18) / bounds.Width, (minimapRect.Height - 18) / bounds.Height);
        var offsetX = minimapRect.X + 9 - bounds.X * scale;
        var offsetY = minimapRect.Y + 9 - bounds.Y * scale;
        var mapNodes = nodes.ToDictionary(node => node.Id, node => new Vector2(
            (float)((node.PositionX ?? 0) * scale + offsetX),
            (float)((node.PositionY ?? 0) * scale + offsetY)));

        ds.FillRoundedRectangle(minimapRect, 8, 8, Color.FromArgb(170, 8, 13, 24));
        ds.DrawRoundedRectangle(minimapRect, 8, 8, Color.FromArgb(110, 54, 247, 255), 1);

        foreach (var edge in edges)
        {
            if (mapNodes.TryGetValue(edge.SourceNodeId, out var source) && mapNodes.TryGetValue(edge.TargetNodeId, out var target))
            {
                ds.DrawLine(source, target, Color.FromArgb(80, 54, 247, 255), 1);
            }
        }

        foreach (var node in nodes)
        {
            var point = mapNodes[node.Id];
            var color = ColorFor(node.ColorKey);
            ds.FillCircle(point.X, point.Y, node.Id == ViewModel?.SelectedNode?.Id ? 4.3f : 2.8f, Color.FromArgb(210, color.R, color.G, color.B));
        }

        var topLeft = ToWorld(new Point(0, 0));
        var bottomRight = ToWorld(new Point(canvas.ActualWidth, canvas.ActualHeight));
        var viewport = new Rect(
            topLeft.X * scale + offsetX,
            topLeft.Y * scale + offsetY,
            Math.Max((bottomRight.X - topLeft.X) * scale, 8),
            Math.Max((bottomRight.Y - topLeft.Y) * scale, 8));
        ds.FillRectangle(viewport, Color.FromArgb(22, 255, 200, 87));
        ds.DrawText("MINIMAP", new Rect(minimapRect.X + 10, minimapRect.Y + minimapRect.Height - 23, 90, 18), Color.FromArgb(130, 168, 183, 196), metaFormat);
    }

    private void CenterViewportFromMinimap(Point point)
    {
        var viewModel = ViewModel;
        if (viewModel is null || viewModel.Nodes.Count == 0)
        {
            return;
        }

        var bounds = GetWorldBounds(viewModel.Nodes);
        var scale = Math.Min((minimapRect.Width - 18) / bounds.Width, (minimapRect.Height - 18) / bounds.Height);
        var worldX = (point.X - minimapRect.X - 9) / scale + bounds.X;
        var worldY = (point.Y - minimapRect.Y - 9) / scale + bounds.Y;
        panX = canvas.ActualWidth / 2 - worldX * zoom;
        panY = canvas.ActualHeight / 2 - worldY * zoom;
        InvalidateCanvas();
    }

    private static Rect GetWorldBounds(IReadOnlyCollection<Node> nodes)
    {
        var minX = nodes.Min(node => node.PositionX ?? 0) - 120;
        var minY = nodes.Min(node => node.PositionY ?? 0) - 120;
        var maxX = nodes.Max(node => node.PositionX ?? 0) + 120;
        var maxY = nodes.Max(node => node.PositionY ?? 0) + 120;
        return new Rect(minX, minY, Math.Max(maxX - minX, 1), Math.Max(maxY - minY, 1));
    }

    private static IReadOnlyList<Node> GetVisibleNodes(MainPageViewModel viewModel)
    {
        if (viewModel.GraphFilterType == "All")
        {
            return viewModel.Nodes.ToList();
        }

        return viewModel.Nodes
            .Where(node => node.Type.Equals(viewModel.GraphFilterType, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static IReadOnlyList<Edge> GetVisibleEdges(MainPageViewModel viewModel, IReadOnlyCollection<Node> visibleNodes)
    {
        var ids = visibleNodes.Select(node => node.Id).ToHashSet();
        return viewModel.Edges
            .Where(edge => ids.Contains(edge.SourceNodeId) && ids.Contains(edge.TargetNodeId))
            .ToList();
    }

    private static float NodeRadius(Node node, int degree)
    {
        return (float)(13 + Math.Clamp(node.Importance, 1, 5) * 3.2 + Math.Min(degree, 8) * 1.7);
    }

    private static Color ColorFor(string key)
    {
        return key.ToLowerInvariant() switch
        {
            "magenta" => Color.FromArgb(255, 255, 79, 216),
            "violet" => Color.FromArgb(255, 150, 110, 255),
            "amber" => Color.FromArgb(255, 255, 200, 87),
            "orange" => Color.FromArgb(255, 255, 143, 82),
            "green" => Color.FromArgb(255, 91, 231, 146),
            "lime" => Color.FromArgb(255, 174, 247, 88),
            "blue" => Color.FromArgb(255, 91, 169, 255),
            "teal" => Color.FromArgb(255, 73, 225, 204),
            "rose" => Color.FromArgb(255, 255, 105, 135),
            "pink" => Color.FromArgb(255, 255, 132, 213),
            "yellow" => Color.FromArgb(255, 255, 238, 128),
            _ => Color.FromArgb(255, 54, 247, 255)
        };
    }

    public double ZoomValue
    {
        get => zoom;
        set { zoom = value; InvalidateCanvas(); }
    }

    public double PanXValue
    {
        get => panX;
        set { panX = value; InvalidateCanvas(); }
    }

    public double PanYValue
    {
        get => panY;
        set { panY = value; InvalidateCanvas(); }
    }

    private void InvalidateCanvas()
    {
        if (canvas.IsLoaded && canvas.ActualWidth > 0 && canvas.ActualHeight > 0)
        {
            canvas.Invalidate();
        }
    }
}
