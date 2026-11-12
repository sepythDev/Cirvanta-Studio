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

    private static readonly Dictionary<int, ComponentKind> ComponentKinds = new()
    {
        [8003] = ComponentKind.LimitSwitchNO, 
        [8006] = ComponentKind.LimitSwitchNC, 
        [3000] = ComponentKind.PowerPhase,
        [3001] = ComponentKind.PowerNeutral,
        [3002] = ComponentKind.PowerEarth,
        [3004] = ComponentKind.PowerThreePhaseEarth,
        [3005] = ComponentKind.PowerThreePhaseEarth,
        [3006] = ComponentKind.PowerThreePhaseNeutralEarth,
        [3010] = ComponentKind.PowerPlus,
        [3011] = ComponentKind.PowerMinus,

        [6000] = ComponentKind.Breaker1P,
        [6007] = ComponentKind.ThermalRelay,
        [6008] = ComponentKind.Breaker2P,
        [6009] = ComponentKind.Breaker3P,
        [6010] = ComponentKind.Breaker1P,
        [6011] = ComponentKind.Breaker3PN,
        [6014] = ComponentKind.Rcd1PN,

        [2000] = ComponentKind.AuxContactNO,
        [2001] = ComponentKind.Contactor3P,
        [2002] = ComponentKind.Contactor4P,
        [2005] = ComponentKind.AuxContactNO,
        [2006] = ComponentKind.AuxContactNO,
        [2007] = ComponentKind.AuxContactNO_NC,

        [8000] = ComponentKind.PushButtonNO,
        [8001] = ComponentKind.PushButtonNC,
        [8002] = ComponentKind.PushButtonNO_NC,
        [8004] = ComponentKind.ToggleSwitchNO,
        [8005] = ComponentKind.ToggleSwitchNC,
        [8018] = ComponentKind.ThermalRelayAuxNO_NC,

        [9000] = ComponentKind.Coil,
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

        const double scaleX = 28.0 / 6.0; // 4.6667
        const double scaleY = 4.2;
        const double offsetX = 50.0;
        const double offsetY = 30.0;

        var records = ParseRecords(rawData);
        var components = new List<ComponentModel>();
        var wires = new List<WireModel>();
        var importedSymbols = new HashSet<(int Code, string Tag, CadPoint Origin)>();

        var cadPinMap = new Dictionary<CadPoint, (Guid ComponentId, string TerminalId)>();

        // 1. Создание компонентов
        foreach (var record in records.Where(r => ComponentKinds.ContainsKey(r.Code)))
        {
            if (!TryReadIntPoint(record, 8, out var origin)) continue;

            var tag = record.Parts.Skip(1).FirstOrDefault(p => p.StartsWith("-", StringComparison.Ordinal))?.Trim()
                      ?? (ComponentTerminals.IsPowerMarker(ComponentKinds[record.Code]) ? "X1" : "Q1");
            tag = tag.TrimStart('-');

            if (!importedSymbols.Add((record.Code, tag, origin))) continue;

            var kind = ComponentKinds[record.Code];

            // Базовые координаты
            var comp = new ComponentModel
            {
                Kind = kind,
                Tag = tag,
                X = Math.Round((offsetX + origin.X * scaleX) / 10.0) * 10.0,
                Y = Math.Round((offsetY + origin.Y * scaleY + (ComponentTerminals.IsPowerMarker(kind) ? -20.0 : 32.0)) / 10.0) * 10.0,
                Rotation = ComponentTerminals.IsPowerMarker(kind) ? 90 : 0
            };

            // Выравнивание первой фазы: первая клемма строго совпадает с origin.X * scaleX
            var allTerms = ComponentTerminals.Get(comp);
            if (allTerms.Count > 0)
            {
                var rad = comp.Rotation * Math.PI / 180.0;
                double GetTermScreenX(TerminalDefinition t) => comp.X + t.OffsetX * Math.Cos(rad) - t.OffsetY * Math.Sin(rad);

                var minScreenX = allTerms.Min(GetTermScreenX);
                var targetFirstX = offsetX + origin.X * scaleX;
                comp.X += Math.Round((targetFirstX - minScreenX) / 10.0) * 10.0;
            }

            components.Add(comp);

            // Регистрация клемм в CADe-пространстве
            RegisterComponentTerminals(cadPinMap, comp, kind, origin);
        }

        // 2. Узлы UNION
        foreach (var record in records.Where(r => r.Code == 20000))
        {
            if (!TryReadIntPoint(record, 2, out var pt) && !TryReadIntPoint(record, 8, out pt)) continue;
            var junc = new ComponentModel
            {
                Kind = ComponentKind.WireJunction,
                Tag = "",
                X = Math.Round((offsetX + pt.X * scaleX) / 10.0) * 10.0,
                Y = Math.Round((offsetY + pt.Y * scaleY) / 10.0) * 10.0
            };
            components.Add(junc);
            cadPinMap[pt] = (junc.Id, "J");
        }

        // 3. Чтение кабелей
        var rawSegments = new List<RawSegment>();
        foreach (var record in records.Where(r => r.Code is >= 4000 and <= 4009))
        {
            if (!TryReadCableSegment(record, out var p1, out var p2)) continue;

            var colorKey = record.Code switch
            {
                4001 => "Blue",
                4002 or 4009 => "GreenYellow",
                4003 => "Red",
                4004 => "Blue",
                _ => "Brown"
            };

            rawSegments.Add(new RawSegment(p1, p2, colorKey));
        }

        // 4. Трассировка соединений
        BuildWiresFromCadGraph(rawSegments, cadPinMap, components, wires, scaleX, scaleY, offsetX, offsetY);

        var project = new ProjectDocument
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Components = components,
            Wires = wires
        };

        // 5. Список неподдерживаемых элементов для всплывающего окна
        var knownNonComponentCodes = new HashSet<int>
        {
            4000, 4001, 4002, 4003, 4004, 4005, 4006, 4007, 4008, 4009, 20000,
            10000, 10001, 10002, 11000, 11001, 12000, 12001, 13000, 14000
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

    private static string GetCadSymbolDescription(int code) => code switch
    {
        >= 1500 and <= 1599 => "Частотный преобразователь / УПП",
        >= 5000 and <= 5099 => "Трансформатор",
        >= 7000 and <= 7099 => "Реле времени (таймер)",
        >= 8003 and <= 8009 => "Концевой выключатель / датчик",
        >= 16000 and <= 16999 => "Модуль ПЛК / PLC",
        _ => "Элемент автоматики"
    };

    private static void RegisterComponentTerminals(
        Dictionary<CadPoint, (Guid ComponentId, string TerminalId)> map,
        ComponentModel comp,
        ComponentKind kind,
        CadPoint origin)
    {
        var x = origin.X;
        var y = origin.Y;

        var allTerms = ComponentTerminals.Get(comp);
        if (allTerms.Count == 0) return;

        var rad = comp.Rotation * Math.PI / 180.0;
        double ScreenX(TerminalDefinition t) => comp.X + t.OffsetX * Math.Cos(rad) - t.OffsetY * Math.Sin(rad);

        // Для блоков питания строго берем клеммы слева направо (L1 -> L2 -> L3 -> PE)
        if (ComponentTerminals.IsPowerMarker(kind))
        {
            var sortedTerms = allTerms.OrderBy(ScreenX).ToList();
            for (var i = 0; i < sortedTerms.Count; i++)
                map[new CadPoint(x + i * 6, y)] = (comp.Id, sortedTerms[i].Id);
            return;
        }

        var topTerms = allTerms.Where(t => t.OffsetY <= 0).OrderBy(ScreenX).ToList();
        var bottomTerms = allTerms.Where(t => t.OffsetY > 0).OrderBy(ScreenX).ToList();

        for (var i = 0; i < topTerms.Count; i++)
        {
            var step = (kind == ComponentKind.ThermalRelayAuxNO_NC) ? 9 : 6;
            map[new CadPoint(x + i * step, y)] = (comp.Id, topTerms[i].Id);
        }

        var heights = kind switch
        {
            ComponentKind.Breaker1P or ComponentKind.Breaker2P or ComponentKind.Breaker3P or ComponentKind.Breaker3PN => new[] { 21 },
            ComponentKind.Coil or ComponentKind.Lamp or ComponentKind.BlinkingLamp => new[] { 15, 18 },
            _ => new[] { 12, 15 }
        };

        foreach (var h in heights)
        {
            for (var i = 0; i < bottomTerms.Count; i++)
            {
                var step = (kind == ComponentKind.ThermalRelayAuxNO_NC) ? 9 : 6;
                map[new CadPoint(x + i * step, y + h)] = (comp.Id, bottomTerms[i].Id);
            }
        }
    }

    private static void BuildWiresFromCadGraph(
        List<RawSegment> segments,
        Dictionary<CadPoint, (Guid ComponentId, string TerminalId)> cadPinMap,
        List<ComponentModel> components,
        List<WireModel> resultWires,
        double scaleX, double scaleY, double offsetX, double offsetY)
    {
        var graph = new Dictionary<CadPoint, List<(CadPoint Next, string Color)>>();
        void AddEdge(CadPoint a, CadPoint b, string col)
        {
            if (!graph.TryGetValue(a, out var la)) graph[a] = la = new();
            if (!graph.TryGetValue(b, out var lb)) graph[b] = lb = new();
            la.Add((b, col));
            lb.Add((a, col));
        }

        foreach (var s in segments)
            AddEdge(s.Start, s.End, s.ColorKey);

        foreach (var (pt, neighbors) in graph)
        {
            if (neighbors.Count >= 3 && !cadPinMap.ContainsKey(pt))
            {
                var junc = new ComponentModel
                {
                    Kind = ComponentKind.WireJunction,
                    Tag = "",
                    X = Math.Round((offsetX + pt.X * scaleX) / 10.0) * 10.0,
                    Y = Math.Round((offsetY + pt.Y * scaleY) / 10.0) * 10.0
                };
                components.Add(junc);
                cadPinMap[pt] = (junc.Id, "J");
            }
        }

        var visitedEdges = new HashSet<(CadPoint, CadPoint)>();

        foreach (var (startPt, startTerm) in cadPinMap)
        {
            if (!graph.TryGetValue(startPt, out var edges)) continue;

            foreach (var (nextPt, color) in edges)
            {
                var edgeKey1 = (startPt, nextPt);
                var edgeKey2 = (nextPt, startPt);
                if (visitedEdges.Contains(edgeKey1) || visitedEdges.Contains(edgeKey2)) continue;

                var waypoints = new List<WireWaypoint>();
                var current = nextPt;
                var prev = startPt;
                visitedEdges.Add(edgeKey1);

                while (!cadPinMap.ContainsKey(current))
                {
                    waypoints.Add(new WireWaypoint
                    {
                        X = Math.Round((offsetX + current.X * scaleX) / 10.0) * 10.0,
                        Y = Math.Round((offsetY + current.Y * scaleY) / 10.0) * 10.0
                    });

                    var nextEdges = graph[current].Where(e => e.Next != prev).ToList();
                    if (nextEdges.Count != 1) break;

                    prev = current;
                    current = nextEdges[0].Next;
                    visitedEdges.Add((prev, current));
                }

                if (cadPinMap.TryGetValue(current, out var endTerm) &&
                    !(startTerm.ComponentId == endTerm.ComponentId && startTerm.TerminalId == endTerm.TerminalId))
                {
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
    }

    private static bool TryReadIntPoint(CadRecord record, int offset, out CadPoint point)
    {
        var fields = record.Parts.Last().Split('*', StringSplitOptions.RemoveEmptyEntries);
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
        var fields = record.Parts.Last().Split('*', StringSplitOptions.RemoveEmptyEntries);
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
            var fields = trimmed.Split('*', StringSplitOptions.RemoveEmptyEntries);

            if (fields.Length >= 2 && int.TryParse(fields[0], out var nextId) &&
                int.TryParse(fields[1], out var nextCode) && nextCode >= 1000)
            {
                if (current is not null) records.Add(new CadRecord(id, code, current.ToArray()));
                id = nextId;
                code = nextCode;
                current = new List<string> { trimmed };
            }
            else current?.Add(trimmed);
        }

        if (current is not null) records.Add(new CadRecord(id, code, current.ToArray()));
        return records;
    }
}