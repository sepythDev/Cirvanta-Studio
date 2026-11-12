namespace CircuitForge.Models;

public sealed record TerminalDefinition(string Id, double OffsetX, double OffsetY, string Label);

public static class ComponentTerminals
{
    // Shared phase pitch keeps terminals of adjacent multi-pole devices aligned.
    public const double PoleSpacing = 28.0;

    public static bool IsPowerMarker(ComponentKind kind) => kind is
        ComponentKind.PowerPhase or
        ComponentKind.PowerNeutral or
        ComponentKind.PowerEarth or
        ComponentKind.PowerPhaseNeutral or
        ComponentKind.PowerPhaseNeutralEarth or
        ComponentKind.PowerThreePhase or
        ComponentKind.PowerThreePhaseEarth or
        ComponentKind.PowerThreePhaseNeutral or
        ComponentKind.PowerThreePhaseNeutralEarth or
        ComponentKind.PowerPlus or
        ComponentKind.PowerMinus or
        ComponentKind.PowerDcEarth;

    public static IReadOnlyList<string> GetPowerConductorLabels(ComponentKind kind) => kind switch
    {
        ComponentKind.PowerPhase => new[] { "L" },
        ComponentKind.PowerNeutral => new[] { "N" },
        ComponentKind.PowerEarth => new[] { "PE" },
        ComponentKind.PowerPhaseNeutral => new[] { "L", "N" },
        ComponentKind.PowerPhaseNeutralEarth => new[] { "L", "N", "PE" },
        ComponentKind.PowerThreePhase => new[] { "L1", "L2", "L3" },
        ComponentKind.PowerThreePhaseEarth => new[] { "L1", "L2", "L3", "PE" },
        ComponentKind.PowerThreePhaseNeutral => new[] { "L1", "L2", "L3", "N" },
        ComponentKind.PowerThreePhaseNeutralEarth => new[] { "L1", "L2", "L3", "N", "PE" },
        ComponentKind.PowerPlus => new[] { "+" },
        ComponentKind.PowerMinus => new[] { "−" },
        ComponentKind.PowerDcEarth => new[] { "PE" },
        _ => Array.Empty<string>()
    };

    public static IReadOnlyList<string> GetPowerConductorLabels(ComponentKind kind, int rotation)
    {
        var labels = GetPowerConductorLabels(kind);
        var angle = ((rotation % 360) + 360) % 360;
        // The marker's conductor row runs right-to-left when rotated 90 degrees.
        // Reverse its logical order so the visible left-to-right order stays L1, L2, L3, N, PE.
        return angle == 90 ? labels.Reverse().ToArray() : labels;
    }

    public static IReadOnlyList<TerminalDefinition> Get(ComponentModel component)
    {
        if (component.Kind == ComponentKind.WireJunction)
            return new[] { new TerminalDefinition("J", 0, 0, "J") };
        if (component.Kind == ComponentKind.Coil)
            return new[] { new TerminalDefinition("A1", 0, -30, "A1"), new TerminalDefinition("A2", 0, 30, "A2") };
        if (component.Kind is ComponentKind.Lamp or ComponentKind.BlinkingLamp)
            return new[] { new TerminalDefinition("L", 0, -32, "1"), new TerminalDefinition("R", 0, 32, "2") };
        if (component.Kind == ComponentKind.Breaker1P)
            return new[] { new TerminalDefinition("L", 0, -32, "1"), new TerminalDefinition("R", 0, 32, "2") };
        var poles = component.Kind switch
        {
            ComponentKind.Breaker1P or ComponentKind.SurgeProtector1P => 1,
            ComponentKind.Breaker1PN or ComponentKind.Breaker2P or ComponentKind.Rcd1PN => 2,
            ComponentKind.Contactor2P => 2,
            ComponentKind.Contactor3P => 3,
            ComponentKind.Contactor4P => 4,
            ComponentKind.Breaker3P => 3,
            ComponentKind.Breaker3PN or ComponentKind.Rcd3PN or ComponentKind.SurgeProtector3PN => 4,
            ComponentKind.ThermalRelay => 3,
            _ => 0
        };
        if (poles > 0)
        {
            var terminals = new List<TerminalDefinition>(poles * 2);
            var spacing = PoleSpacing;
            var firstX = 50 - spacing * (poles - 1) / 2;
            for (var i = 0; i < poles; i++)
            {
                var isContactor = component.Kind is ComponentKind.Contactor2P or ComponentKind.Contactor3P or ComponentKind.Contactor4P;
                var label = isContactor ? $"P{i + 1}" : i == 0 ? "L" : i == 1 ? "N" : $"L{i + 1}";
                var poleOffsetX = firstX + i * spacing - 50;
                var upperNumber = isContactor ? (i * 2 + 1).ToString() : $"{label}1";
                var lowerNumber = isContactor ? (i * 2 + 2).ToString() : $"{label}2";
                terminals.Add(new TerminalDefinition($"{label}in", poleOffsetX, -32, upperNumber));
                terminals.Add(new TerminalDefinition($"{label}out", poleOffsetX, 32, lowerNumber));
            }
            return terminals;
        }
        if (IsPowerMarker(component.Kind))
        {
            var labels = GetPowerConductorLabels(component.Kind, component.Rotation);
            var terminals = new List<TerminalDefinition>(labels.Count);
            var step = labels.Count switch
            {
                1 => 0.0,
                _ => PoleSpacing
            };
            var firstY = -step * (labels.Count - 1) / 2.0;
            for (var i = 0; i < labels.Count; i++)
                terminals.Add(new TerminalDefinition(labels[i], 40, firstY + i * step, labels[i]));
            return terminals;
        }

        if (component.Kind is ComponentKind.PushButtonNO or ComponentKind.PushButtonNC
            or ComponentKind.LimitSwitchNO or ComponentKind.LimitSwitchNC 
            or ComponentKind.AuxContactNO or ComponentKind.AuxContactNC or ComponentKind.OverloadNO
            or ComponentKind.ToggleSwitchNO or ComponentKind.ToggleSwitchNC)
        {
            // Stable terminal ids, with upper/lower terminals matching the vertical NO/NC symbol.
            // The current project order determines the visible numbers in MainWindow.
            return new[]
            {
                new TerminalDefinition("L", 8, -30, "1"),
                new TerminalDefinition("R", 8, 30, "2")
            };
        }
        if (component.Kind == ComponentKind.PushButtonNO_NC)
        {
            return new[]
            {
                new TerminalDefinition("NC_L", -18, -30, "11"),
                new TerminalDefinition("NC_R", -18, 30, "12"),
                new TerminalDefinition("NO_L", 18, -30, "13"),
                new TerminalDefinition("NO_R", 18, 30, "14")
            };
        }
        if (component.Kind == ComponentKind.ThermalRelayAuxNO_NC)
        {
            return new[]
            {
                new TerminalDefinition("NC_L", -18, -30, "95"),
                new TerminalDefinition("NC_R", -18, 30, "96"),
                new TerminalDefinition("NO_L", 18, -30, "97"),
                new TerminalDefinition("NO_R", 18, 30, "98")
            };
        }
        if (component.Kind == ComponentKind.ToggleSwitchNO_NC)
        {
            return new[]
            {
                new TerminalDefinition("NC_L", -18, -30, "1"),
                new TerminalDefinition("NC_R", -18, 30, "2"),
                new TerminalDefinition("NO_L", 18, -30, "3"),
                new TerminalDefinition("NO_R", 18, 30, "4")
            };
        }
        if (component.Kind == ComponentKind.ThermalRelayAuxNC)
        {
            return new[]
            {
                new TerminalDefinition("L", 8, -30, "95"),
                new TerminalDefinition("R", 8, 30, "96")
            };
        }
        if (component.Kind == ComponentKind.AuxContactNO_NC)
        {
            return new[]
            {
                new TerminalDefinition("NC_L", -18, -30, "11"),
                new TerminalDefinition("NC_R", -18, 30, "12"),
                new TerminalDefinition("NO_L", 18, -30, "13"),
                new TerminalDefinition("NO_R", 18, 30, "14")
            };
        }
        if (component.Kind is ComponentKind.ConContact or ComponentKind.PushButtonCon or ComponentKind.ToggleSwitchCon)
        {
            return new[]
            {
                new TerminalDefinition("C", 0, -30, "11"),
                new TerminalDefinition("NC", -24, 30, "12"),
                new TerminalDefinition("NO", 24, 30, "14")
            };
        }
        if (component.Kind == ComponentKind.CrossoverSwitch)
        {
            return new[]
            {
                new TerminalDefinition("L1", -40, -12, "1"),
                new TerminalDefinition("L2", -40, 12, "2"),
                new TerminalDefinition("R1", 40, -12, "3"),
                new TerminalDefinition("R2", 40, 12, "4")
            };
        }
        if (component.Kind == ComponentKind.AcMotor1P)
        {
            return new[]
            {
                new TerminalDefinition("L", -24, -29, "U"),
                new TerminalDefinition("N", 24, -29, "V")
            };
        }
        if (component.Kind == ComponentKind.AcMotor)
        {
            return new[]
            {
                new TerminalDefinition("L1", -24, -29, "U"),
                new TerminalDefinition("L2", 0, -34, "V"),
                new TerminalDefinition("L3", 24, -29, "W")
            };
        }
        if (component.Kind == ComponentKind.AcSupply && component.Phases == 3)
        {
            return new[]
            {
                new TerminalDefinition("L", -40, -18, "L1"),
                new TerminalDefinition("L2", -40, -4, "L2"),
                new TerminalDefinition("L3", -40, 10, "L3"),
                new TerminalDefinition("R", 40, -4, "N")
            };
        }
        if (component.Kind == ComponentKind.AcSupply)
        {
            return new[]
            {
                new TerminalDefinition("L", -40, -4, "L"),
                new TerminalDefinition("R", 40, -4, "N")
            };
        }
        return new[]
        {
            new TerminalDefinition("L", -40, -4, "1"),
            new TerminalDefinition("R", 40, -4, "2")
        };
    }
}
