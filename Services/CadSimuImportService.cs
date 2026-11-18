using CircuitForge.Models;
using System.Globalization;
using System.IO;
using System.Text;

namespace CircuitForge.Services;

public sealed class CadSimuImportService
{
    private sealed record CadRecord(int Id, int Code, string[] Parts);
    private readonly record struct CadPoint(int X, int Y);
    private sealed record RawSegment(CadPoint Start, CadPoint End, string ColorKey);

    private sealed record ImportedItem(
        int Code,
        ComponentModel Component,
        CadPoint Origin,
        int CadWidthUnits,
        int CadHeightUnits,
        int PoleCount,
        int PoleStepCadX,
        int PoleStepCadY);

    private static readonly Dictionary<int, ComponentKind> ComponentKinds = new()
    {
        [3000] = ComponentKind.PowerPhase,
        [3001] = ComponentKind.PowerNeutral,
        [3002] = ComponentKind.PowerEarth,
        [3004] = ComponentKind.PowerThreePhase,
        [3005] = ComponentKind.PowerThreePhaseEarth,
        [3006] = ComponentKind.PowerThreePhaseNeutral,
        [3010] = ComponentKind.PowerPlus,
        [3011] = ComponentKind.PowerMinus,

        [6000] = ComponentKind.Breaker1P,
        [6005] = ComponentKind.Breaker2P,
        [6006] = ComponentKind.Breaker3PN,
        [6007] = ComponentKind.ThermalRelay,
        [6008] = ComponentKind.Breaker2P,
        [6009] = ComponentKind.Breaker3P,
        [6010] = ComponentKind.Breaker1P,
        [6011] = ComponentKind.Breaker3PN,
        [6014] = ComponentKind.Rcd1PN,

        [2000] = ComponentKind.Contactor2P,
        [2001] = ComponentKind.Contactor3P,
        [2002] = ComponentKind.Contactor4P,
        [2005] = ComponentKind.AuxContactNO,
        [2006] = ComponentKind.AuxContactNO,
        [2007] = ComponentKind.AuxContactNO_NC,

        [7000] = ComponentKind.AuxContactNO,
        [7001] = ComponentKind.AuxContactNC,
        [7004] = ComponentKind.TimerOnContactNO,
        [7005] = ComponentKind.TimerOnContactNC,
        [7006] = ComponentKind.TimerOnOffContactNO_NC,
        [7008] = ComponentKind.TimerOffContactNO,
        [7009] = ComponentKind.TimerOffContactNC,
        [7010] = ComponentKind.TimerOnOffContactNO_NC,

        [8000] = ComponentKind.PushButtonNO,
        [8001] = ComponentKind.PushButtonNC,
        [8002] = ComponentKind.PushButtonNO_NC,
        [8003] = ComponentKind.LimitSwitchNO,
        [8004] = ComponentKind.ToggleSwitchNO,
        [8005] = ComponentKind.ToggleSwitchNC,
        [8006] = ComponentKind.LimitSwitchNC,
        [8008] = ComponentKind.ToggleSwitchNO,
        [8018] = ComponentKind.ThermalRelayAuxNO_NC,

        [9000] = ComponentKind.Coil,
        [9004] = ComponentKind.TimerOnDelay,
        [9005] = ComponentKind.TimerOffDelay,
        [9006] = ComponentKind.TimerOnOffDelay,
        [9008] = ComponentKind.Lamp,
        [9009] = ComponentKind.BlinkingLamp,
        [1000] = ComponentKind.AcMotor,
        [1001] = ComponentKind.AcMotor1P,
        [1002] = ComponentKind.DcMotor
    };

    public sealed record ImportResult(ProjectDocument Project, IReadOnlyList<string> UnsupportedComponents);

    public ImportResult Import(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string text;
        try { text = File.ReadAllText(path, Encoding.GetEncoding("windows-1251")); }
        catch { text = File.ReadAllText(path, Encoding.UTF8); }

        if (!text.StartsWith("CADe_SIMU*", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Файл не распознан как проект CADe SIMU.");

        var rawData = text.Replace("CADe_SIMU*", "");
        var records = ParseRecords(rawData);

        var importedItems = new List<ImportedItem>();
        var importedSymbols = new HashSet<(int Code, string Tag, CadPoint Origin)>();
        var explicitJunctions = new HashSet<CadPoint>();
        var rawSegments = new List<RawSegment>();

        // 1. Сбор компонентов
        foreach (var record in records.Where(r => ComponentKinds.ContainsKey(r.Code)))
        {
            if (!TryReadIntPoint(record, 8, out var origin)) continue;

            var kind = ComponentKinds[record.Code];
            var tag = record.Parts.Skip(1).FirstOrDefault(p => p.StartsWith("-", StringComparison.Ordinal))?.Trim()
                      ?? (ComponentTerminals.IsPowerMarker(kind) ? "X1" : "Q1");
            tag = tag.TrimStart('-');

            if (!importedSymbols.Add((record.Code, tag, origin))) continue;

            var rotation = 0;
            var fields = GetDataFields(record);

            // Определяем ориентацию (угол поворота)
            if (ComponentTerminals.IsPowerMarker(kind))
            {
                if (TryReadIntPoint(record, 14, out var boxSize))
                    rotation = boxSize.X >= boxSize.Y ? 90 : 0;
                else
                    rotation = 90;
            }
            else
            {
                var orientCode = 0;
                if (fields.Length > 17 && int.TryParse(fields[17], NumberStyles.Integer, CultureInfo.InvariantCulture, out var o))
                    orientCode = o;

                rotation = orientCode switch
                {
                    1 => 90,
                    2 => 180,
                    3 => 270,
                    _ => 0
                };
            }

            var comp = new ComponentModel
            {
                Kind = kind,
                Tag = tag,
                Rotation = rotation
            };

            var (cadW, cadH, poles, stepX, stepY) = GetCadGeometry(record.Code, kind, rotation);
            importedItems.Add(new ImportedItem(record.Code, comp, origin, cadW, cadH, poles, stepX, stepY));
        }

        // 2. Сбор проводов и узлов соединений (4001)
        foreach (var record in records.Where(r => r.Code is >= 4000 and <= 4019))
        {
            if (!TryReadCableSegment(record, out var p1, out var p2)) continue;

            if (record.Code is 4001 or 4002 || p1 == p2)
            {
                explicitJunctions.Add(p1);
                continue;
            }

            var colorKey = record.Code switch
            {
                4009 or 4004 => "Blue",          
                4010 => "GreenYellow",           
                4003 or 4018 => "Red",           
                4019 => "Black",                 
                _ => "Brown"                     
            };
            rawSegments.Add(new RawSegment(p1, p2, colorKey));
        }

        // 3. Собираем все CAD-координаты для расчёта сетки 10 px
        var allCadX = new HashSet<int>();
        var allCadY = new HashSet<int>();

        foreach (var pt in explicitJunctions)
        {
            allCadX.Add(pt.X);
            allCadY.Add(pt.Y);
        }
        foreach (var seg in rawSegments)
        {
            allCadX.Add(seg.Start.X); allCadX.Add(seg.End.X);
            allCadY.Add(seg.Start.Y); allCadY.Add(seg.End.Y);
        }
        foreach (var item in importedItems)
        {
            for (var p = 0; p < item.PoleCount; p++)
            {
                if (ComponentTerminals.IsPowerMarker(item.Component.Kind) && item.Component.Rotation == 0)
                {
                    allCadX.Add(item.Origin.X);
                    allCadY.Add(item.Origin.Y - p * item.PoleStepCadY);
                }
                else
                {
                    allCadX.Add(item.Origin.X + p * item.PoleStepCadX);
                    allCadY.Add(item.Origin.Y + p * item.PoleStepCadY);
                    if (item.CadHeightUnits > 0)
                        allCadY.Add(item.Origin.Y + item.CadHeightUnits);
                    if (item.CadWidthUnits > 0)
                        allCadX.Add(item.Origin.X + item.CadWidthUnits);
                }
            }
        }

        var xConstraints = new List<(int A, int B, double MinDist)>();
        var yConstraints = new List<(int A, int B, double MinDist)>();
        var polePitch = ComponentTerminals.PoleSpacing;

        foreach (var item in importedItems)
        {
            if (ComponentTerminals.IsPowerMarker(item.Component.Kind))
            {
                if (item.Component.Rotation == 0 && item.PoleCount > 1)
                {
                    for (var p = 0; p < item.PoleCount - 1; p++)
                    {
                        var yUpper = item.Origin.Y - (p + 1) * item.PoleStepCadY;
                        var yLower = item.Origin.Y - p * item.PoleStepCadY;
                        yConstraints.Add((yUpper, yLower, polePitch));
                    }
                }
                else if (item.Component.Rotation == 90 && item.PoleCount > 1)
                {
                    for (var p = 0; p < item.PoleCount - 1; p++)
                        xConstraints.Add((item.Origin.X + p * item.PoleStepCadX, item.Origin.X + (p + 1) * item.PoleStepCadX, polePitch));
                }
                continue;
            }

            if (item.PoleCount > 1)
            {
                var requiredSpan = item.Component.Kind is ComponentKind.ThermalRelayAuxNO_NC or ComponentKind.TimerOnOffContactNO_NC
                    ? 40.0
                    : (item.PoleCount - 1) * polePitch;
                if (item.PoleStepCadX > 0)
                    xConstraints.Add((item.Origin.X, item.Origin.X + (item.PoleCount - 1) * item.PoleStepCadX, requiredSpan));
                if (item.PoleStepCadY > 0)
                    yConstraints.Add((item.Origin.Y, item.Origin.Y + (item.PoleCount - 1) * item.PoleStepCadY, requiredSpan));
            }

            if (item.CadHeightUnits > 0)
                yConstraints.Add((item.Origin.Y, item.Origin.Y + item.CadHeightUnits, 60.0));
            if (item.CadWidthUnits > 0)
                xConstraints.Add((item.Origin.X, item.Origin.X + item.CadWidthUnits, 60.0));
        }

        var mapX = ComputeAxisPositions(allCadX, xConstraints, 80.0);
        var mapY = ComputeAxisPositions(allCadY, yConstraints, 80.0);

        // 4. Позиционирование компонентов и регистрация выводов
        var components = new List<ComponentModel>();
        var cadPinMap = new Dictionary<CadPoint, (Guid ComponentId, string TerminalId)>();

        foreach (var item in importedItems)
        {
            var comp = item.Component;
            var allTerms = ComponentTerminals.Get(comp);

            if (ComponentTerminals.IsPowerMarker(comp.Kind))
            {
                if (comp.Rotation == 0)
                {
                    var yTop = item.Origin.Y - Math.Max(0, item.PoleCount - 1) * item.PoleStepCadY;
                    var yBot = item.Origin.Y;
                    comp.X = mapX[item.Origin.X] - 40.0;
                    comp.Y = Snap((mapY[yTop] + mapY[yBot]) / 2.0);
                }
                else
                {
                    var xLeft = item.Origin.X;
                    var xRight = item.Origin.X + Math.Max(0, item.PoleCount - 1) * item.PoleStepCadX;
                    comp.X = Snap((mapX[xLeft] + mapX[xRight]) / 2.0);
                    comp.Y = mapY[item.Origin.Y] - 40.0;
                }
            }
            else if (comp.Rotation is 90 or 270)
            {
                // Горизонтальный элемент
                var xLeft = item.Origin.X;
                var xRight = item.Origin.X + (item.CadWidthUnits > 0 ? item.CadWidthUnits : 0);
                comp.X = Snap((mapX[xLeft] + mapX[xRight]) / 2.0);

                var yTop = item.Origin.Y;
                var yBot = item.Origin.Y + Math.Max(0, item.PoleCount - 1) * item.PoleStepCadY;
                comp.Y = Snap((mapY[yTop] + mapY[yBot]) / 2.0);
            }
            else
            {
                // Вертикальный элемент
                var xLeft = item.Origin.X;
                var xRight = item.Origin.X + Math.Max(0, item.PoleCount - 1) * item.PoleStepCadX;
                comp.X = Snap((mapX[xLeft] + mapX[xRight]) / 2.0);

                if (item.CadHeightUnits > 0)
                    comp.Y = Snap((mapY[item.Origin.Y] + mapY[item.Origin.Y + item.CadHeightUnits]) / 2.0);
                else
                    comp.Y = mapY[item.Origin.Y] + 30.0;
            }

            components.Add(comp);
            RegisterComponentTerminals(cadPinMap, item, allTerms);
        }

        // Узлы 4001
        foreach (var pt in explicitJunctions)
        {
            if (cadPinMap.ContainsKey(pt)) continue;
            var junc = new ComponentModel
            {
                Kind = ComponentKind.WireJunction,
                Tag = "",
                X = mapX[pt.X],
                Y = mapY[pt.Y]
            };
            components.Add(junc);
            cadPinMap[pt] = (junc.Id, "J");
        }

        // 5. Прокладка проводов
        var wires = new List<WireModel>();
        BuildWiresFromCadGraph(rawSegments, cadPinMap, components, wires, mapX, mapY);

        var project = new ProjectDocument
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Components = components,
            Wires = wires
        };

        var knownNonComponentCodes = new HashSet<int>
        {
            2, 8, 4000, 4001, 4002, 4003, 4004, 4005, 4006, 4007, 4008, 4009, 4010,
            4011, 4012, 4013, 4014, 4015, 4016, 4017, 4018, 4019,
            20000, 10000, 10001, 10002, 11000, 11001, 12000, 12001, 13000, 14000
        };

        var missing = records
            .Where(r => r.Code >= 1000 && !ComponentKinds.ContainsKey(r.Code) && !knownNonComponentCodes.Contains(r.Code))
            .Select(r =>
            {
                var tag = r.Parts.Skip(1).FirstOrDefault(p => p.StartsWith("-", StringComparison.Ordinal))?.Trim() ?? "";
                var desc = GetCadSymbolDescription(r.Code);
                return string.IsNullOrWhiteSpace(tag)
                    ? $"{desc} (код {r.Code})"
                    : $"{tag}: {desc} (код {r.Code})";
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ImportResult(project, missing);
    }

    private static (int CadW, int CadH, int Poles, int StepX, int StepY) GetCadGeometry(int code, ComponentKind kind, int rotation)
    {
        if (ComponentTerminals.IsPowerMarker(kind))
        {
            var count = ComponentTerminals.GetPowerConductorLabels(kind).Count;
            return rotation == 0
                ? (0, Math.Max(0, count - 1) * 6, count, 0, 6)
                : (Math.Max(0, count - 1) * 6, 0, count, 6, 0);
        }

        var isHorizontal = rotation is 90 or 270;

        return code switch
        {
            2000 => isHorizontal ? (12, 6, 2, 0, 6) : (6, 12, 2, 6, 0),
            2001 => isHorizontal ? (12, 12, 3, 0, 6) : (12, 12, 3, 6, 0),
            2002 => isHorizontal ? (12, 18, 4, 0, 6) : (18, 12, 4, 6, 0),
            2005 or 2006 => isHorizontal ? (12, 6, 2, 0, 6) : (6, 12, 2, 6, 0),
            6005 => isHorizontal ? (18, 6, 2, 0, 6) : (6, 18, 2, 6, 0),
            6006 => isHorizontal ? (18, 18, 4, 0, 6) : (18, 18, 4, 6, 0),
            6007 => isHorizontal ? (12, 12, 3, 0, 6) : (12, 12, 3, 6, 0),
            6008 or 6014 => isHorizontal ? (21, 6, 2, 0, 6) : (6, 21, 2, 6, 0),
            6009 => isHorizontal ? (21, 12, 3, 0, 6) : (12, 21, 3, 6, 0),
            6011 => isHorizontal ? (21, 18, 4, 0, 6) : (18, 21, 4, 6, 0),
            6000 or 6010 => isHorizontal ? (21, 0, 1, 0, 0) : (0, 21, 1, 0, 0),
            8018 or 7006 or 7010 => isHorizontal ? (12, 9, 2, 0, 9) : (9, 12, 2, 9, 0),
            8002 or 2007 => isHorizontal ? (12, 6, 2, 0, 6) : (6, 12, 2, 6, 0),
            1000 => (12, 0, 3, 6, 0),
            1001 => (6, 0, 2, 6, 0),
            _ => isHorizontal ? (12, 0, 1, 0, 0) : (0, 12, 1, 0, 0)
        };
    }

    private static void RegisterComponentTerminals(
        Dictionary<CadPoint, (Guid ComponentId, string TerminalId)> map,
        ImportedItem item,
        IReadOnlyList<TerminalDefinition> allTerms)
    {
        if (allTerms.Count == 0) return;
        var comp = item.Component;
        var x = item.Origin.X;
        var y = item.Origin.Y;

        if (ComponentTerminals.IsPowerMarker(comp.Kind))
        {
            if (comp.Rotation == 0)
            {
                if (comp.Kind == ComponentKind.PowerThreePhaseNeutral)
                {
                    map[new CadPoint(x, y - 18)] = (comp.Id, "N");
                    map[new CadPoint(x, y - 12)] = (comp.Id, "L3");
                    map[new CadPoint(x, y - 6)] = (comp.Id, "L2");
                    map[new CadPoint(x, y)] = (comp.Id, "L1");
                }
                else
                {
                    var sortedY = allTerms.OrderBy(t => t.OffsetY).ToList();
                    for (var i = 0; i < sortedY.Count; i++)
                        map[new CadPoint(x, y - (sortedY.Count - 1 - i) * item.PoleStepCadY)] = (comp.Id, sortedY[i].Id);
                }
            }
            else
            {
                var rad = comp.Rotation * Math.PI / 180.0;
                var sortedX = allTerms.OrderBy(t => t.OffsetX * Math.Cos(rad) - t.OffsetY * Math.Sin(rad)).ToList();
                for (var i = 0; i < sortedX.Count; i++)
                    map[new CadPoint(x + i * item.PoleStepCadX, y)] = (comp.Id, sortedX[i].Id);
            }
            return;
        }

        if (comp.Rotation is 90 or 270)
        {
            // Горизонтальный элемент: входные клеммы слева (X), выходные справа (X + CadW)
            var inTerms = allTerms.Where(t => t.OffsetY <= 0).OrderBy(t => t.OffsetX).ToList();
            var outTerms = allTerms.Where(t => t.OffsetY > 0).OrderBy(t => t.OffsetX).ToList();

            for (var i = 0; i < inTerms.Count; i++)
                map[new CadPoint(x, y + i * item.PoleStepCadY)] = (comp.Id, inTerms[i].Id);

            if (item.CadWidthUnits > 0)
            {
                for (var i = 0; i < outTerms.Count; i++)
                    map[new CadPoint(x + item.CadWidthUnits, y + i * item.PoleStepCadY)] = (comp.Id, outTerms[i].Id);
            }
            return;
        }

        // Вертикальный элемент
        var topTerms = allTerms.Where(t => t.OffsetY <= 0).OrderBy(t => t.OffsetX).ToList();
        var bottomTerms = allTerms.Where(t => t.OffsetY > 0).OrderBy(t => t.OffsetX).ToList();

        for (var i = 0; i < topTerms.Count; i++)
            map[new CadPoint(x + i * item.PoleStepCadX, y)] = (comp.Id, topTerms[i].Id);

        if (item.CadHeightUnits > 0)
        {
            for (var i = 0; i < bottomTerms.Count; i++)
                map[new CadPoint(x + i * item.PoleStepCadX, y + item.CadHeightUnits)] = (comp.Id, bottomTerms[i].Id);
        }
    }

    private static Dictionary<int, double> ComputeAxisPositions(
        IReadOnlyCollection<int> rawCoords,
        IReadOnlyList<(int A, int B, double MinDist)> constraints,
        double startOffset)
    {
        var sorted = rawCoords.Distinct().OrderBy(v => v).ToList();
        var pos = new Dictionary<int, double>(sorted.Count);
        if (sorted.Count == 0) return pos;

        var indexOf = new Dictionary<int, int>(sorted.Count);
        for (var i = 0; i < sorted.Count; i++)
            indexOf[sorted[i]] = i;

        var incoming = new List<(int PrevIdx, double MinDist)>[sorted.Count];
        for (var i = 0; i < sorted.Count; i++)
            incoming[i] = new();

        foreach (var (a, b, minDist) in constraints)
        {
            if (!indexOf.TryGetValue(a, out var ia) || !indexOf.TryGetValue(b, out var ib)) continue;
            if (ia == ib) continue;
            if (ia > ib) (ia, ib) = (ib, ia);
            incoming[ib].Add((ia, Math.Ceiling(minDist / 10.0) * 10.0));
        }

        var coords = new double[sorted.Count];
        coords[0] = startOffset;

        for (var i = 1; i < sorted.Count; i++)
        {
            var delta = sorted[i] - sorted[i - 1];
            var baseStep = Math.Max(10.0, Math.Round((delta * 10.0 / 3.0) / 10.0) * 10.0);
            var next = coords[i - 1] + baseStep;

            foreach (var (prevIdx, minDist) in incoming[i])
                next = Math.Max(next, coords[prevIdx] + minDist);

            coords[i] = next;
        }

        for (var i = 0; i < sorted.Count; i++)
            pos[sorted[i]] = coords[i];

        return pos;
    }

    private static void BuildWiresFromCadGraph(
        List<RawSegment> rawSegments,
        Dictionary<CadPoint, (Guid ComponentId, string TerminalId)> cadPinMap,
        List<ComponentModel> components,
        List<WireModel> resultWires,
        Dictionary<int, double> mapX,
        Dictionary<int, double> mapY)
    {
        var splitPoints = new HashSet<CadPoint>(cadPinMap.Keys);
        foreach (var s in rawSegments)
        {
            splitPoints.Add(s.Start);
            splitPoints.Add(s.End);
        }

        var splitSegments = new List<RawSegment>();
        var seenElementary = new HashSet<(CadPoint, CadPoint)>();

        foreach (var s in rawSegments)
        {
            if (s.Start == s.End) continue;

            if (s.Start.Y == s.End.Y)
            {
                var y = s.Start.Y;
                var minX = Math.Min(s.Start.X, s.End.X);
                var maxX = Math.Max(s.Start.X, s.End.X);
                var pts = splitPoints.Where(p => p.Y == y && p.X >= minX && p.X <= maxX)
                    .OrderBy(p => p.X).ToList();
                for (var i = 0; i < pts.Count - 1; i++)
                    AddUniqueSegment(pts[i], pts[i + 1], s.ColorKey);
            }
            else if (s.Start.X == s.End.X)
            {
                var x = s.Start.X;
                var minY = Math.Min(s.Start.Y, s.End.Y);
                var maxY = Math.Max(s.Start.Y, s.End.Y);
                var pts = splitPoints.Where(p => p.X == x && p.Y >= minY && p.Y <= maxY)
                    .OrderBy(p => p.Y).ToList();
                for (var i = 0; i < pts.Count - 1; i++)
                    AddUniqueSegment(pts[i], pts[i + 1], s.ColorKey);
            }
            else
            {
                AddUniqueSegment(s.Start, s.End, s.ColorKey);
            }
        }

        void AddUniqueSegment(CadPoint a, CadPoint b, string color)
        {
            if (a == b) return;
            var key = (a.X < b.X || (a.X == b.X && a.Y < b.Y)) ? (a, b) : (b, a);
            if (seenElementary.Add(key))
                splitSegments.Add(new RawSegment(a, b, color));
        }

        var graph = new Dictionary<CadPoint, List<(CadPoint Next, string Color)>>();
        foreach (var s in splitSegments)
        {
            if (!graph.TryGetValue(s.Start, out var la)) graph[s.Start] = la = new();
            if (!graph.TryGetValue(s.End, out var lb)) graph[s.End] = lb = new();
            la.Add((s.End, s.ColorKey));
            lb.Add((s.Start, s.ColorKey));
        }

        foreach (var (pt, neighbors) in graph)
        {
            if (neighbors.Count >= 3 && !cadPinMap.ContainsKey(pt))
            {
                var junc = new ComponentModel
                {
                    Kind = ComponentKind.WireJunction,
                    Tag = "",
                    X = mapX[pt.X],
                    Y = mapY[pt.Y]
                };
                components.Add(junc);
                cadPinMap[pt] = (junc.Id, "J");
            }
        }

        var compById = components.ToDictionary(c => c.Id);
        var visitedEdges = new HashSet<(CadPoint, CadPoint)>();

        foreach (var (startPt, startTerm) in cadPinMap)
        {
            if (!graph.TryGetValue(startPt, out var edges)) continue;

            foreach (var (nextPt, color) in edges)
            {
                if (visitedEdges.Contains((startPt, nextPt)) || visitedEdges.Contains((nextPt, startPt)))
                    continue;

                var rawWaypoints = new List<CadPoint>();
                var current = nextPt;
                var prev = startPt;
                visitedEdges.Add((startPt, nextPt));

                while (!cadPinMap.ContainsKey(current))
                {
                    rawWaypoints.Add(current);
                    var nextEdges = graph[current].Where(e => e.Next != prev).ToList();
                    if (nextEdges.Count != 1) break;

                    prev = current;
                    current = nextEdges[0].Next;
                    visitedEdges.Add((prev, current));
                }

                if (!cadPinMap.TryGetValue(current, out var endTerm) ||
                    (startTerm.ComponentId == endTerm.ComponentId && startTerm.TerminalId == endTerm.TerminalId))
                    continue;

                var startWorld = GetTerminalWorldPos(compById[startTerm.ComponentId], startTerm.TerminalId);
                var endWorld = GetTerminalWorldPos(compById[endTerm.ComponentId], endTerm.TerminalId);

                var waypoints = new List<WireWaypoint>();
             foreach (var wp in rawWaypoints)
                 waypoints.Add(new WireWaypoint { X = mapX[wp.X], Y = mapY[wp.Y] });

             for (var i = waypoints.Count - 1; i > 0; i--)
             {
                 if (Math.Abs(waypoints[i].X - waypoints[i - 1].X) < 0.5 &&
                     Math.Abs(waypoints[i].Y - waypoints[i - 1].Y) < 0.5)
                     waypoints.RemoveAt(i);
             }
                resultWires.Add(new WireModel
                {
                    StartComponentId = startTerm.ComponentId,
                    StartTerminalId = startTerm.TerminalId,
                    EndComponentId = endTerm.ComponentId,
                    EndTerminalId = endTerm.TerminalId,
                    ColorKey = color,
                    Waypoints = waypoints
                });
            }
        }
    }

    private static (double X, double Y) GetTerminalWorldPos(ComponentModel c, string terminalId)
    {
        var term = ComponentTerminals.Get(c).FirstOrDefault(t => t.Id == terminalId);
        if (term is null) return (c.X, c.Y);
        var rad = ((c.Rotation % 360) + 360) % 360 * Math.PI / 180.0;
        return (
            c.X + term.OffsetX * Math.Cos(rad) - term.OffsetY * Math.Sin(rad),
            c.Y + term.OffsetX * Math.Sin(rad) + term.OffsetY * Math.Cos(rad)
        );
    }

    private static double Snap(double value) => Math.Round(value / 10.0) * 10.0;

    private static string GetCadSymbolDescription(int code) => code switch
    {
        >= 1500 and <= 1599 => "Частотный преобразователь / УПП",
        >= 5000 and <= 5099 => "Трансформатор",
        >= 7000 and <= 7099 => "Реле времени (таймер)",
        >= 8003 and <= 8009 => "Концевой выключатель / датчик",
        >= 8020 and <= 8029 => "Кнопка пульта управления",
        9004 => "Катушка реле времени (таймера)",
        >= 16000 and <= 16999 => "Модуль ПЛК / PLC",
        _ => "Элемент автоматики"
    };

    private static string[] GetDataFields(CadRecord record)
    {
        var last = record.Parts.Last();
        var starIdx = last.IndexOf('*');
        var data = starIdx >= 0 ? last[(starIdx + 1)..] : last;
        return data.Split('*', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool TryReadIntPoint(CadRecord record, int offset, out CadPoint point)
    {
        var fields = GetDataFields(record);
        if (fields.Length > offset + 1 &&
            int.TryParse(fields[offset], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) &&
            int.TryParse(fields[offset + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
        {
            point = new CadPoint(x, y);
            return true;
        }
        point = default;
        return false;
    }

    private static bool TryReadCableSegment(CadRecord record, out CadPoint p1, out CadPoint p2)
    {
        var fields = GetDataFields(record);
        if (fields.Length >= 12 &&
            int.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x1) &&
            int.TryParse(fields[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y1) &&
            int.TryParse(fields[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x2) &&
            int.TryParse(fields[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y2))
        {
            p1 = new CadPoint(x1, y1);
            p2 = new CadPoint(x2, y2);
            return true;
        }

        p1 = default;
        p2 = default;
        return false;
    }

    private static List<CadRecord> ParseRecords(string text)
    {
        var records = new List<CadRecord>();
        List<string>? current = null;
        int id = 0, code = 0;

        foreach (var part in text.Split('#'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("$$$", StringComparison.Ordinal))
                break;

            var fields = trimmed.Split('*', StringSplitOptions.RemoveEmptyEntries);

            if (fields.Length == 2 &&
                int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var nextId) &&
                int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var nextCode) &&
                nextCode > 0)
            {
                if (current is not null) records.Add(new CadRecord(id, code, current.ToArray()));
                id = nextId;
                code = nextCode;
                current = new List<string> { trimmed };
                continue;
            }

            if (current is not null && code == 8)
            {
                var rawTokens = trimmed.Split('*');
                if (rawTokens.Length >= 3 &&
                    int.TryParse(rawTokens[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var capNextId) &&
                    int.TryParse(rawTokens[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var capNextCode) &&
                    capNextCode > 0)
                {
                    current.Add(string.Join("*", rawTokens[..^2]));
                    records.Add(new CadRecord(id, code, current.ToArray()));
                    id = capNextId;
                    code = capNextCode;
                    current = new List<string> { $"{capNextId}*{capNextCode}" };
                    continue;
                }
            }

            current?.Add(trimmed);
        }

        if (current is not null) records.Add(new CadRecord(id, code, current.ToArray()));
        return records;
    }
}