namespace CircuitForge.Models;
public sealed class ComponentModel
{
    public double DelayOnSeconds { get; set; } = 3.0;
    public bool EnableTon { get; set; } = true;
    public double DelayOffSeconds { get; set; } = 3.0;
    public bool EnableTof { get; set; } = true;
    public Guid Id { get; set; } = Guid.NewGuid();
    public ComponentKind Kind { get; set; }
    public string Tag { get; set; } = "X1";
    public double X { get; set; }
    public double Y { get; set; }
    public int Phases { get; set; } = 1;
    public int Voltage { get; set; } = 230;
    public int Rotation { get; set; }
    public string GlowColorHex { get; set; } = "#FDE047";
}
