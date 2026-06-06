using Argus.Core.Models;

namespace Argus.Core.Graph;

public sealed class ForceDirectedGraphLayout
{
    public void Apply(IList<Node> nodes, IReadOnlyList<Edge> edges, double width = 1100, double height = 700)
    {
        if (nodes.Count == 0)
        {
            return;
        }

        InitializeMissingPositions(nodes, width, height);

        var byId = nodes.ToDictionary(node => node.Id);
        var area = Math.Max(width * height, 1);
        var k = Math.Sqrt(area / Math.Max(nodes.Count, 1));
        var temperature = Math.Min(width, height) / 8;

        for (var iteration = 0; iteration < 80; iteration++)
        {
            var delta = nodes.ToDictionary(node => node.Id, _ => (X: 0.0, Y: 0.0));

            for (var i = 0; i < nodes.Count; i++)
            {
                for (var j = i + 1; j < nodes.Count; j++)
                {
                    var a = nodes[i];
                    var b = nodes[j];
                    var dx = (a.PositionX ?? 0) - (b.PositionX ?? 0);
                    var dy = (a.PositionY ?? 0) - (b.PositionY ?? 0);
                    var distance = Math.Max(Math.Sqrt(dx * dx + dy * dy), 0.01);
                    var force = k * k / distance;
                    var nx = dx / distance * force;
                    var ny = dy / distance * force;

                    delta[a.Id] = (delta[a.Id].X + nx, delta[a.Id].Y + ny);
                    delta[b.Id] = (delta[b.Id].X - nx, delta[b.Id].Y - ny);
                }
            }

            foreach (var edge in edges)
            {
                if (!byId.TryGetValue(edge.SourceNodeId, out var source) ||
                    !byId.TryGetValue(edge.TargetNodeId, out var target))
                {
                    continue;
                }

                var dx = (source.PositionX ?? 0) - (target.PositionX ?? 0);
                var dy = (source.PositionY ?? 0) - (target.PositionY ?? 0);
                var distance = Math.Max(Math.Sqrt(dx * dx + dy * dy), 0.01);
                var force = distance * distance / k * Math.Clamp(edge.Strength, 0.15, 2.0);
                var nx = dx / distance * force;
                var ny = dy / distance * force;

                delta[source.Id] = (delta[source.Id].X - nx, delta[source.Id].Y - ny);
                delta[target.Id] = (delta[target.Id].X + nx, delta[target.Id].Y + ny);
            }

            foreach (var node in nodes)
            {
                var movement = delta[node.Id];
                var length = Math.Max(Math.Sqrt(movement.X * movement.X + movement.Y * movement.Y), 0.01);
                var limited = Math.Min(length, temperature);
                var x = (node.PositionX ?? 0) + movement.X / length * limited;
                var y = (node.PositionY ?? 0) + movement.Y / length * limited;
                node.PositionX = Math.Clamp(x, 80, width - 80);
                node.PositionY = Math.Clamp(y, 80, height - 80);
            }

            temperature *= 0.94;
        }
    }

    private static void InitializeMissingPositions(IList<Node> nodes, double width, double height)
    {
        var radius = Math.Min(width, height) * 0.34;
        var centerX = width / 2;
        var centerY = height / 2;

        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].PositionX.HasValue && nodes[i].PositionY.HasValue)
            {
                continue;
            }

            var angle = Math.PI * 2 * i / Math.Max(nodes.Count, 1);
            nodes[i].PositionX = centerX + Math.Cos(angle) * radius;
            nodes[i].PositionY = centerY + Math.Sin(angle) * radius;
        }
    }
}
