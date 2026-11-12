namespace CircuitForge.Models;
public sealed class ProjectDocument
{
    public int SchemaVersion { get; set; } = 4;
    public string Name { get; set; } = "Untitled";
    public List<ComponentModel> Components { get; set; } = new();
    public List<WireModel> Wires { get; set; } = new();
}
