using System.Collections.Generic;

namespace CircuitForge.Models;

public sealed class WireModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StartComponentId { get; set; }
    public string StartTerminalId { get; set; } = "L";
    public Guid EndComponentId { get; set; }
    public string EndTerminalId { get; set; } = "R";
    public string ColorKey { get; set; } = "Brown";

    // Intermediate orthogonal route points. Missing in old project files means a straight auto-route.
    public List<WireWaypoint> Waypoints { get; set; } = new();
}

public sealed class WireWaypoint
{
    public double X { get; set; }
    public double Y { get; set; }
}
