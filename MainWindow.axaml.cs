#pragma warning disable CS0618 // drag-and-drop / DataObject are flagged obsolete in newer Avalonia 11.3 builds
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Styling;
using CircuitForge.Models;
using CircuitForge.Services;
using CircuitForge.Simulation;
using DoubleCollection = Avalonia.Collections.AvaloniaList<double>;
using PointCollection = Avalonia.Collections.AvaloniaList<Avalonia.Point>;
namespace CircuitForge;

public partial class MainWindow : Window
{
    private readonly ProjectFileService _files = new();
    private readonly CadSimuImportService _cadImporter = new();
    private string? _currentFilePath;
    private readonly CircuitSimulator _simulator = new();
    private readonly DispatcherTimer _blinkTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _currentFlowTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private double _currentDotOffset;
    private int _currentFlowTickCount;
    private bool _blinkOn = true;
    private readonly List<CurrentFlowDotVisual> _currentFlowDots = new();
    private readonly List<BlinkingLampVisual> _blinkingLampVisuals = new();
    private readonly List<PaletteItem> _palette = new();
    private Button? _activeCategoryButton;
    private readonly Stack<ProjectDocument> _undoStack = new();
    private const int MaxUndoSteps = 100;
    private bool _dragUndoSaved;
    private bool _darkTheme;
    private ProjectDocument _project = new();
    private ComponentModel? _selected;
    private Guid? _selectedWireId;
    private double _zoom = 1.0;
    private readonly ScaleTransform _zoomTransform = new(1.0, 1.0);
    private readonly TranslateTransform _panTransform = new(0, 0);
    private bool _panning;
    private IPointer? _pointer;
    private PaletteItem? _dragInFlight;
    private int _gridCount = -1;
    private Point _panLastPoint;
    private ComponentKind? _placingKind;
    private int _placingRotation;
    private bool _wireMode;
    private readonly List<(Guid ComponentId, string TerminalId)> _terminalBatchSource = new();
    private readonly List<(Guid ComponentId, string TerminalId)> _terminalBatchTarget = new();
    private Guid? _terminalBatchSourceComponent;
    private Guid? _terminalBatchTargetComponent;
    private Point? _terminalPressPosition;
    private TerminalRef? _wireStart;
    private TerminalRef? _hoverTerminal;
    private Guid? _pressedButtonId;
    private Point _cursorPosition = new(120, 120);
    private Point? _paletteMouseDownPoint;
    private PaletteItem? _paletteDragItem;
    private bool _dragging;
    private readonly HashSet<Guid> _selectedComponentIds = new();
    private bool _selectingArea;
    private Point _selectionStart;
    private Point _selectionCurrent;
    private Point _dragStart;
    private readonly Dictionary<Guid, Point> _dragOrigins = new();
    private readonly Dictionary<Guid, List<Point>> _wireDragOrigins = new();
    private Guid? _movingWireId;
    private int _movingWireSegment;
    private Point _wireMoveStart;
    private List<Point>? _wireMoveOriginalPath;
    private bool _wireMoveUndoSaved;
    private readonly List<Point> _wireWaypoints = new();
    private string _selectedWireColorKey = "Brown";
    private int _pendingAcPhases = 1;
    private int _pendingAcVoltage = 230;
    private const double GridSize = 10;


    private static readonly (string Key, string Label, string Hex)[] WireColors =
    {
        ("Brown", "Коричневый", "#8B4513"),
        ("Black", "Чёрный", "#202124"),
        ("Grey", "Серый", "#6B7280"),
        ("Blue", "Синий", "#2563EB"),
        ("GreenYellow", "Жёлто-зелёный", "#65A30D"),
        ("Red", "Красный", "#DC2626")
    };

    private static readonly (string Label, string Hex)[] LampGlowColors =
    {
        ("Жёлтый", "#FDE047"), ("Оранжевый", "#FB923C"), ("Красный", "#F87171"),
        ("Зелёный", "#4ADE80")
    };

    public MainWindow()
    {
        InitializeComponent();
        LocalizationService.LanguageChanged += ApplyLanguage;
        Opened += (_, _) =>
        {
            FitToWorkArea();
            if (string.IsNullOrWhiteSpace(_currentFilePath)) RestoreCameraState(null);
        };
        Closing += (_, _) => SaveCameraState();
        BuildPalette();
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkOn = !_blinkOn;
            if (_simulator.IsRunning && _project.Components.Any(c => c.Kind == ComponentKind.BlinkingLamp && _simulator.IsEnergized(c.Id)))
                UpdateBlinkingLampVisuals();
        };
        _currentFlowTimer.Interval = TimeSpan.FromMilliseconds(60);
        _currentFlowTimer.Tick += (_, _) =>
        {
            try
            {
                if (!_simulator.IsRunning || !_project.Wires.Any(IsCurrentFlowWire)) return;
                _currentDotOffset = (_currentDotOffset + 0.8) % 24;
                if (++_currentFlowTickCount >= 20)
                {
                    _currentFlowTickCount = 0;
                    var previousFlow = _project.Wires
                        .Where(IsCurrentFlowWire)
                        .Select(wire => (wire.Id, Forward: _simulator.IsCurrentFlowForward(wire.Id)))
                        .ToHashSet();
                    _simulator.AdvanceCurrentFlowSequence();
                    var nextFlow = _project.Wires
                        .Where(IsCurrentFlowWire)
                        .Select(wire => (wire.Id, Forward: _simulator.IsCurrentFlowForward(wire.Id)))
                        .ToHashSet();
                    if (previousFlow.SetEquals(nextFlow)) UpdateCurrentFlowDots();
                    else Redraw();
                }
                else UpdateCurrentFlowDots();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Ошибка анимации: {ex.Message}";
            }
        };
        var viewTransform = new TransformGroup();
        viewTransform.Children.Add(_zoomTransform);
        viewTransform.Children.Add(_panTransform);
        SchematicCanvas.RenderTransform = viewTransform;
        SchematicCanvas.RenderTransformOrigin = RelativePoint.TopLeft;
        DragDrop.SetAllowDrop(Viewport, true);
        Viewport.AddHandler(DragDrop.DragOverEvent, Canvas_DragOver);
        Viewport.AddHandler(DragDrop.DropEvent, Canvas_Drop);
        Viewport.AddHandler(InputElement.PointerWheelChangedEvent, Canvas_MouseWheel, RoutingStrategies.Tunnel);
        Viewport.AddHandler(InputElement.PointerPressedEvent, Canvas_MiddleMouseDown, RoutingStrategies.Tunnel);
        Viewport.AddHandler(InputElement.PointerMovedEvent, Canvas_MiddleMouseMove, RoutingStrategies.Tunnel);
        Viewport.AddHandler(InputElement.PointerReleasedEvent, Canvas_MiddleMouseUp, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, Window_PreviewKeyDown, RoutingStrategies.Tunnel);
        NewProject();
        ApplyLanguage();
    }

    private void LanguageRu_Click(object? sender, RoutedEventArgs e)
    {
    LocalizationService.SetLanguage(AppLanguage.Ru);
    LanguageButton.Content = "RU";
    }

    private void LanguageEn_Click(object? sender, RoutedEventArgs e)
    {
    LocalizationService.SetLanguage(AppLanguage.En);
    LanguageButton.Content = "EN";
    }

    private void LanguageEs_Click(object? sender, RoutedEventArgs e)
    {
    LocalizationService.SetLanguage(AppLanguage.Es);
    LanguageButton.Content = "ES";
    }


    private void FitToWorkArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var workW = screen.WorkingArea.Width / scale;
        var workH = screen.WorkingArea.Height / scale;
        if (Width > workW) Width = workW;
        if (Height > workH) Height = workH;
        MinWidth = Math.Min(900, Width);
        MinHeight = Math.Min(540, Height);
    }

    private void FitSchematicToViewport()
    {
        var bounds = new List<Point>();
        foreach (var component in _project.Components)
        {
            bounds.Add(new Point(component.X - 65, component.Y - 50));
            bounds.Add(new Point(component.X + 65, component.Y + 50));
        }
        foreach (var wire in _project.Wires)
        {
            var a = _project.Components.FirstOrDefault(c => c.Id == wire.StartComponentId);
            var b = _project.Components.FirstOrDefault(c => c.Id == wire.EndComponentId);
            if (a is null || b is null) continue;
            bounds.Add(TerminalPosition(a, wire.StartTerminalId));
            bounds.Add(TerminalPosition(b, wire.EndTerminalId));
            bounds.AddRange(wire.Waypoints.Select(p => new Point(p.X, p.Y)));
        }
        if (bounds.Count == 0) { _zoom = 1; _panTransform.X = 0; _panTransform.Y = 0; }
        else
        {
            var left = bounds.Min(p => p.X); var right = bounds.Max(p => p.X);
            var top = bounds.Min(p => p.Y); var bottom = bounds.Max(p => p.Y);
            var width = Math.Max(1, right - left); var height = Math.Max(1, bottom - top);
            const double margin = 48;
            _zoom = Math.Clamp(Math.Min((Viewport.Bounds.Width - margin * 2) / width, (Viewport.Bounds.Height - margin * 2) / height), 0.2, 3.0);
            _panTransform.X = (Viewport.Bounds.Width - width * _zoom) / 2 - left * _zoom;
            _panTransform.Y = (Viewport.Bounds.Height - height * _zoom) / 2 - top * _zoom;
        }
        _zoomTransform.ScaleX = _zoom; _zoomTransform.ScaleY = _zoom;
        SaveCameraState();
        StatusText.Text = "Схема целиком помещена в рабочую область.";
    }

    private string CameraStatePath => System.IO.Path.Combine(AppContext.BaseDirectory, "camera-state.json");

    private void RestoreCameraState(string? projectPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectPath)) ResetCameraToGridCenter();
            else
            {
                var states = System.IO.File.Exists(CameraStatePath)
                    ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CameraState>>(System.IO.File.ReadAllText(CameraStatePath))
                    : null;
                var key = System.IO.Path.GetFullPath(projectPath);
                if (states is null || !states.TryGetValue(key, out var state)) ResetCameraToGridCenter();
                else if (double.IsFinite(state.Zoom) && double.IsFinite(state.X) && double.IsFinite(state.Y))
                { _zoom = Math.Clamp(state.Zoom, 0.2, 3.0); _panTransform.X = state.X; _panTransform.Y = state.Y; }
            }
            _zoomTransform.ScaleX = _zoom; _zoomTransform.ScaleY = _zoom;
        }
        catch { /* Ignore an unreadable preference and use the default view. */ }
    }

    private void ResetCameraToGridCenter()
    {
        _zoom = 1;
        _panTransform.X = (Viewport.Bounds.Width - SchematicCanvas.Width) / 2;
        _panTransform.Y = (Viewport.Bounds.Height - SchematicCanvas.Height) / 2;
    }

    private void SaveCameraState()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_currentFilePath)) return;
            var states = System.IO.File.Exists(CameraStatePath)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CameraState>>(System.IO.File.ReadAllText(CameraStatePath)) ?? new()
                : new Dictionary<string, CameraState>();
            states[System.IO.Path.GetFullPath(_currentFilePath)] = new CameraState(_zoom, _panTransform.X, _panTransform.Y);
            System.IO.File.WriteAllText(CameraStatePath, System.Text.Json.JsonSerializer.Serialize(states));
        }
        catch { /* Camera preferences are optional. */ }
    }

    // ---- small helpers replacing WPF-only API -------------------------------------------------
    private static double Len(Point p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);
    private static double Len(Vector v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);

    private static Transform ScaleAbout(double sx, double sy, double cx, double cy) =>
        new MatrixTransform(Matrix.CreateTranslation(-cx, -cy) * Matrix.CreateScale(sx, sy) * Matrix.CreateTranslation(cx, cy));

    private static Transform RotateAbout(double degrees, double cx, double cy) =>
        new MatrixTransform(Matrix.CreateTranslation(-cx, -cy) * Matrix.CreateRotation(degrees * Math.PI / 180.0) * Matrix.CreateTranslation(cx, cy));

    private void CaptureCanvas() => _pointer?.Capture(SchematicCanvas);

    private void ReleaseCapture()
    {
        if (_pointer is not null && ReferenceEquals(_pointer.Captured, SchematicCanvas))
            _pointer.Capture(null);
    }

    private bool IsLeft(PointerEventArgs e) => e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed;

    private async Task ShowMessageAsync(string title, string message)
    {
        var ok = new Button { Content = "OK", Width = 80, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var dialog = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 14,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, ok }
            }
        };
        ok.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private static string CategoryFor(string title) => title switch
    {
        "Плюс DC" or "Минус DC" or "Земля DC" or
        "Фаза" or "Нейтраль" or "Земля" or
        "Фаза и нейтраль" or "Фаза, нейтраль и земля" or
        "Три фазы" or "Три фазы и земля" or
        "Три фазы и нейтраль" or "Три фазы, нейтраль и земля" => "Электропитание",
        "Автомат" or "Дифференциал" or "Автомат 1P" or "Автомат 1P+N" or "Автомат 2P" or "Автомат 3P" or "Автомат 3P+N" or "УЗО 1P+N" or "УЗО 3P+N" or "Тепловое реле" or "Ограничитель перенапряжения 1P" or "Ограничитель перенапряжения 3P+N" => "Автоматы",
        "Контактор 2-полюсный" or "Контактор 3-полюсный" or "Контактор 4-полюсный" or "Контакт NO" or "Контакт NC" or "Контакт NO+NC" or "CON contact" or "Термореле NO (97-98)" or "Термореле NC (95-96)" or "Термореле NC+NO (95-96/97-98)" => "Контакторы и переключатели",
        "Двигатель DC" or "Двигатель AC 3~" or "Двигатель AC 1~" => "Моторы",
        "Лампа" or "Лампа мигающая" => "Катушки",
        "Кнопка NO" or "Кнопка NC" or "Кнопка NO+NC" or "Кнопка CON" or "Концевик NO" or "Концевик NC" or "Переключатель NO" or "Переключатель NC" or "Переключатель NO+NC" or "Переключатель CON" or "Crossover switch" => "Кнопки",
        // Detector category is reserved for future sensor components.
        "Катушка" => "Катушки",
        "Тепловая защита" => "Реле",
        _ => "Прочее"
    };

    private void FilterPalette(string category)
    {
        PaletteRow.IsVisible = true;
        ToolboxBorder.VerticalAlignment = VerticalAlignment.Top;
        foreach (var button in CategoryPanel.Children.OfType<Button>())
            button.Classes.Set("selected", button == _activeCategoryButton);
        foreach (var child in PalettePanel.Children.OfType<Control>())
            if (child.Tag is string itemCategory)
                child.IsVisible = itemCategory == category;
        StatusText.Text = $"Категория: {category}.";
    }

    private void SelectCategory(string category, object? sender)
    {
        _activeCategoryButton = sender as Button;
        FilterPalette(category);
    }

    private void CategoryPower_Click(object? sender, RoutedEventArgs e) => SelectCategory("Электропитание", sender);
    private void CategoryBreakers_Click(object? sender, RoutedEventArgs e) => SelectCategory("Автоматы", sender);
    private void CategoryContactors_Click(object? sender, RoutedEventArgs e) => SelectCategory("Контакторы и переключатели", sender);
    private void CategoryMotors_Click(object? sender, RoutedEventArgs e) => SelectCategory("Моторы", sender);
    private void CategoryButtons_Click(object? sender, RoutedEventArgs e) => SelectCategory("Кнопки", sender);
    private void CategoryCoils_Click(object? sender, RoutedEventArgs e) => SelectCategory("Катушки", sender);
    private void CategoryRelays_Click(object? sender, RoutedEventArgs e) => SelectCategory("Реле", sender);

    private void BuildPalette()
    {
        AddPalette("Кнопка NO", ComponentKind.PushButtonNO, "S1");
        AddPalette("Кнопка NC", ComponentKind.PushButtonNC, "S0");
        AddPalette("Кнопка NO+NC", ComponentKind.PushButtonNO_NC, "S1");
        AddPalette("Кнопка CON", ComponentKind.PushButtonCon, "S1");
        AddPaletteSeparator("Кнопки");
        AddPalette("Переключатель NO", ComponentKind.ToggleSwitchNO, "SA1");
        AddPalette("Переключатель NC", ComponentKind.ToggleSwitchNC, "SA1");
        AddPalette("Переключатель NO+NC", ComponentKind.ToggleSwitchNO_NC, "SA1");
        AddPalette("Переключатель CON", ComponentKind.ToggleSwitchCon, "SA1");
        AddPalette("Crossover switch", ComponentKind.CrossoverSwitch, "SA1");
        AddPalette("Контакт NO", ComponentKind.AuxContactNO, "K1");
        AddPalette("Контакт NC", ComponentKind.AuxContactNC, "K1");
        AddPaletteSeparator("Контакторы и переключатели");
        AddPalette("Термореле NO (97-98)", ComponentKind.OverloadNO, "F1");
        AddPalette("Термореле NC (95-96)", ComponentKind.ThermalRelayAuxNC, "F2");
        AddPalette("Термореле NC+NO (95-96/97-98)", ComponentKind.ThermalRelayAuxNO_NC, "F2");
        AddPaletteSeparator("Контакторы и переключатели");
        AddPalette("Контакт NO+NC", ComponentKind.AuxContactNO_NC, "K1");
        AddPalette("CON contact", ComponentKind.ConContact, "K1");
        AddPalette("Концевик NO", ComponentKind.LimitSwitchNO, "FC1");
        AddPalette("Концевик NC", ComponentKind.LimitSwitchNC, "FC1");
        AddPaletteSeparator("Контакторы и переключатели");
        AddPalette("Контактор 2-полюсный", ComponentKind.Contactor2P, "KM");
        AddPalette("Контактор 3-полюсный", ComponentKind.Contactor3P, "KM");
        AddPalette("Контактор 4-полюсный", ComponentKind.Contactor4P, "KM");
        AddPalette("Катушка", ComponentKind.Coil, "K1");
        AddPalette("Тепловая защита", ComponentKind.OverloadNC, "F1");
        AddPalette("Автомат 1P", ComponentKind.Breaker1P, "QF1");
        AddPalette("Автомат 1P+N", ComponentKind.Breaker1PN, "QF2");
        AddPalette("Автомат 2P", ComponentKind.Breaker2P, "QF3");
        AddPalette("Автомат 3P", ComponentKind.Breaker3P, "QF4");
        AddPalette("Автомат 3P+N", ComponentKind.Breaker3PN, "QF5");
        AddPaletteSeparator("Автоматы");
        AddPalette("УЗО 1P+N", ComponentKind.Rcd1PN, "QFD2");
        AddPalette("УЗО 3P+N", ComponentKind.Rcd3PN, "QFD3");
        AddPaletteSeparator("Автоматы");
        AddPalette("Тепловое реле", ComponentKind.ThermalRelay, "F2");
        AddPalette("Фаза", ComponentKind.PowerPhase, "X1");
        AddPalette("Нейтраль", ComponentKind.PowerNeutral, "X1");
        AddPalette("Земля", ComponentKind.PowerEarth, "X1");
        AddPalette("Фаза и нейтраль", ComponentKind.PowerPhaseNeutral, "X1");
        AddPalette("Фаза, нейтраль и земля", ComponentKind.PowerPhaseNeutralEarth, "X1");
        AddPaletteSeparator("Электропитание");
        AddPalette("Три фазы", ComponentKind.PowerThreePhase, "X1");
        AddPalette("Три фазы и земля", ComponentKind.PowerThreePhaseEarth, "X1");
        AddPalette("Три фазы и нейтраль", ComponentKind.PowerThreePhaseNeutral, "X1");
        AddPalette("Три фазы, нейтраль и земля", ComponentKind.PowerThreePhaseNeutralEarth, "X1");
        AddPaletteSeparator("Электропитание");
        AddPalette("Плюс DC", ComponentKind.PowerPlus, "XDC1");
        AddPalette("Минус DC", ComponentKind.PowerMinus, "XDC1");
        AddPalette("Земля DC", ComponentKind.PowerDcEarth, "XDC1");
        AddPalette("Лампа", ComponentKind.Lamp, "H1");
        AddPalette("Лампа мигающая", ComponentKind.BlinkingLamp, "H2");
        AddPalette("Двигатель AC 3~", ComponentKind.AcMotor, "M2");
        AddPalette("Двигатель AC 1~", ComponentKind.AcMotor1P, "M3");
        AddPaletteSeparator("Моторы");
        AddPalette("Двигатель DC", ComponentKind.DcMotor, "M1");
    }

    private void AddPalette(string title, ComponentKind? kind, string tag, bool hasSubmenu = false)
    {
        // Keep the palette model in sync with the visible palette buttons.
        // Without this entry, placement calls _palette.First(...) and crashes.
        _palette.Add(new PaletteItem(title, kind, tag, IsWire: kind is null));

        var group = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2, 0, 4, 4),
            Tag = CategoryFor(title),
            IsVisible = false
        };
        var main = new Button
        {
            Padding = new Thickness(6, 5, 6, 5),
            Background = Brush("#F8FAFC"), Foreground = Brush("#1E293B"),
            BorderBrush = Brush("#CBD5E1"), BorderThickness = new Thickness(1), Tag = title
        };
        ToolTip.SetTip(main, LocalizedPaletteTitle(title));
        ToolTip.SetShowDelay(main, 350);
        var body = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var symbol = MiniSymbol(kind, title == "Провод");
        if (symbol is Panel symbolPanel && CategoryFor(title) is "Контакторы и переключатели" or "Кнопки")
            SetSymbolStrokeThicknessForDisplay(symbolPanel, 1.4);
        body.Children.Add(symbol);
        main.Content = body;
        if (kind is not null)
        {
            var paletteItem = _palette[^1];
            main.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
            {
                if (!e.GetCurrentPoint(main).Properties.IsLeftButtonPressed) return;
                _paletteMouseDownPoint = e.GetPosition(main);
                _paletteDragItem = paletteItem;
            }, RoutingStrategies.Tunnel);
            main.AddHandler(InputElement.PointerMovedEvent, async (object? _, PointerEventArgs e) =>
            {
                if (_simulator.IsRunning || !e.GetCurrentPoint(main).Properties.IsLeftButtonPressed ||
                    _paletteMouseDownPoint is not Point start || _paletteDragItem is null) return;
                var current = e.GetPosition(main);
                if (Math.Abs(current.X - start.X) < 4 && Math.Abs(current.Y - start.Y) < 4) return;
                var dragItem = _paletteDragItem;
                _paletteMouseDownPoint = null;
                _paletteDragItem = null;
                _dragInFlight = dragItem;
                try
                {
                    var data = new DataObject();
                    data.Set("circuitforge/palette-item", dragItem.Title);
                    await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
                }
                finally { _dragInFlight = null; }
            }, RoutingStrategies.Tunnel);
        }
        main.Click += (_, _) =>
        {
            if (_simulator.IsRunning)
            {
                StatusText.Text = "Останови симуляцию, чтобы редактировать схему.";
                return;
            }
            _wireMode = title == "Провод";
            _wireStart = null;
            _placingKind = _wireMode ? null : kind;
            _placingRotation = kind is not null && ComponentTerminals.IsPowerMarker(kind.Value) ? 90 : 0;
            _selected = null;
            _selectedComponentIds.Clear();
            _selectedWireId = null;
            if (kind == ComponentKind.AcSupply)
            {
                _pendingAcPhases = 1;
                _pendingAcVoltage = 230;
            }
            StatusText.Text = _wireMode
                ? $"Провод · цвет: {CurrentWireColorLabel()}. Выбери две клеммы."
                : $"Предпросмотр «{title}»: наведи курсор на поле и щёлкни для размещения.";
            Redraw();
        };
        group.Children.Add(main);

        if (hasSubmenu)
        {
            var arrow = new Button
            {
                Content = "▾", Padding = new Thickness(5, 4, 5, 4),
                Background = Brush("#F1F5F9"), Foreground = Brush("#334155"),
                BorderBrush = Brush("#CBD5E1"), BorderThickness = new Thickness(1)
            };
            ToolTip.SetTip(arrow, title == "Провод" ? "Цвет провода" : "Тип и напряжение источника AC");
            arrow.Click += (_, _) =>
            {
                var menu = title == "Провод" ? BuildWireColorMenu(arrow) : BuildAcSourceMenu(arrow);
                menu.Open(arrow);
            };
            group.Children.Add(arrow);
        }
        PalettePanel.Children.Add(group);
    }

    private void AddPaletteSeparator(string category)
    {
        PalettePanel.Children.Add(new Border
        {
            Width = 1,
            Height = 28,
            Background = Brush("#CBD5E1"),
            Margin = new Thickness(6, 0, 6, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = category,
            IsVisible = false,
            IsHitTestVisible = false
        });
    }

    private ContextMenu BuildWireColorMenu(Control target)
    {
        // Preserve the target wire while the context menu takes keyboard/mouse focus.
        var selectedWireIdAtOpen = _selectedWireId;
        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        foreach (var color in WireColors)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border
            {
                Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0),
                Background = Brush(color.Hex), BorderBrush = Brush("#CBD5E1"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3)
            });
            row.Children.Add(new TextBlock { Text = color.Label, VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Header = row, ToggleType = MenuItemToggleType.CheckBox, IsChecked = _selectedWireColorKey == color.Key };
            item.Click += (_, _) =>
            {
                _selectedWireColorKey = color.Key;
                var selectedWireId = selectedWireIdAtOpen ?? _selectedWireId;
                var selectedWire = selectedWireId is Guid id
                    ? _project.Wires.FirstOrDefault(w => w.Id == id)
                    : null;
                if (selectedWire is not null && !_simulator.IsRunning)
                {
                    if (!string.Equals(selectedWire.ColorKey, color.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        PushUndoState();
                        selectedWire.ColorKey = color.Key;
                    }
                    StatusText.Text = $"Цвет выбранного провода изменён на «{color.Label}».";
                }
                else
                {
                    StatusText.Text = $"Цвет новых проводов: {color.Label}.";
                }
                _selectedWireId = null;
                _selected = null;
                _selectedComponentIds.Clear();
                Redraw();
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    private ContextMenu BuildAcSourceMenu(Control target)
    {
        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        menu.Items.Add(AcChoice("1 фаза · 230 В AC", 1, 230));
        menu.Items.Add(AcChoice("1 фаза · 24 В AC", 1, 24));
        menu.Items.Add(new Separator());
        menu.Items.Add(AcChoice("3 фазы · 400 В AC", 3, 400));
        menu.Items.Add(AcChoice("3 фазы · 230 В AC", 3, 230));
        return menu;
    }

    private MenuItem AcChoice(string label, int phases, int voltage)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) =>
        {
            if (_simulator.IsRunning)
            {
                StatusText.Text = "Останови симуляцию, чтобы редактировать схему.";
                return;
            }
            _pendingAcPhases = phases;
            _pendingAcVoltage = voltage;
            _wireMode = false;
            _wireStart = null;
            _placingKind = ComponentKind.AcSupply;
            _placingRotation = 0;
            _selected = null;
            _selectedComponentIds.Clear();
            _selectedWireId = null;
            StatusText.Text = $"Источник AC: {phases} фаза(ы), {voltage} В. Наведи на поле и щёлкни.";
            Redraw();
        };
        return item;
    }

    private Control MiniSymbol(ComponentKind? kind, bool wire)
    {
        var isThreePhaseNeutralEarth = kind == ComponentKind.PowerThreePhaseNeutralEarth;
        var c = new Canvas
        {
            Width = kind == ComponentKind.Breaker3P ? 43 : kind == ComponentKind.Breaker3PN ? 52 : 32,
            Height = isThreePhaseNeutralEarth ? 32 : 26,
            RenderTransform = kind == ComponentKind.Breaker3PN
                ? new TranslateTransform(-2, 0)
                : kind is ComponentKind.Breaker1P or ComponentKind.Breaker1PN or ComponentKind.Breaker2P
                    ? new TransformGroup
                    {
                        Children =
                        {
                            ScaleAbout(0.86, 0.86, 16, 11),
                            new TranslateTransform(kind switch
                            {
                                ComponentKind.Breaker1P => -1.5,
                                ComponentKind.Breaker1PN or ComponentKind.Breaker2P => -3,
                                _ => 0
                            }, 0)
                        }
                    }
                    : null
        };
        var p = Brush("#010101");
        if (wire)
        {
            c.Children.Add(new Line { StartPoint = new Point(2, 13), EndPoint = new Point(30, 13), Stroke = p, StrokeThickness = 2 });
            c.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = p });
            Canvas.SetLeft(c.Children[^1], 0); Canvas.SetTop(c.Children[^1], 10.5);
            c.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = p });
            Canvas.SetLeft(c.Children[^1], 27); Canvas.SetTop(c.Children[^1], 10.5);
            SetSymbolStrokeThickness(c, 1.4);
            return c;
        }

        if (kind is ComponentKind.PowerPhase or ComponentKind.PowerNeutral or ComponentKind.PowerEarth
            or ComponentKind.PowerPhaseNeutral or ComponentKind.PowerPhaseNeutralEarth
            or ComponentKind.PowerThreePhase or ComponentKind.PowerThreePhaseEarth
            or ComponentKind.PowerThreePhaseNeutral or ComponentKind.PowerThreePhaseNeutralEarth
            or ComponentKind.PowerPlus or ComponentKind.PowerMinus or ComponentKind.PowerDcEarth)
        {
            var labels = ComponentTerminals.GetPowerConductorLabels(kind.Value);
            var rowStep = kind == ComponentKind.PowerPhaseNeutral
                ? 12.5
                : isThreePhaseNeutralEarth
                    ? 6.0
                : labels.Contains("PE") && labels.Count > 1
                    ? 19.0 / (labels.Count - 1)
                : 23.0 / Math.Max(1, labels.Count - 1);
            for (var i = 0; i < labels.Count; i++)
            {
                var y = labels.Count == 1
                    ? 13.0
                    : kind == ComponentKind.PowerPhaseNeutral
                        ? 5.0 + i * rowStep
                        : isThreePhaseNeutralEarth
                            ? 3.5 + i * rowStep
                        : labels.Contains("PE")
                            ? 2.5 + i * rowStep
                        : 1.5 + i * rowStep;
                var text = new TextBlock
                {
                    Text = kind == ComponentKind.PowerDcEarth && labels[i] == "PE" ? "" : labels[i],
                    Foreground = p,
                    FontSize = (kind is ComponentKind.PowerPlus or ComponentKind.PowerMinus)
                    ? 11
                    : labels[i].Length > 2 ? 4.7 : kind == ComponentKind.PowerPhaseNeutral ? 5.72 : 5.5,
                    FontWeight = FontWeight.SemiBold
                };
                Canvas.SetLeft(text, kind == ComponentKind.PowerEarth ? 6 : 0);
                var labelOffset = kind is ComponentKind.PowerPlus or ComponentKind.PowerMinus
                    ? 8.0
                    : isThreePhaseNeutralEarth ? 3.5 : 5.0;
                Canvas.SetTop(text, y - labelOffset);
                c.Children.Add(text);
                if (labels[i] == "PE")
                {
                    // Protective-earth glyph followed by its conductor line.
                    var isDcEarthEmblem = kind == ComponentKind.PowerDcEarth;
                    var glyphCenterX = isDcEarthEmblem ? 13.5 : kind == ComponentKind.PowerEarth ? 21.0 : 15.0;
                    var halfWidth = isDcEarthEmblem ? 5.5 : 3.0;
                    var stemTop = isDcEarthEmblem ? 6.5 : 4.0;
                    var stemBottom = isDcEarthEmblem ? 1.5 : 1.0;
                    var bar1Y = y - stemBottom;
                    var bar2Y = y + 1.0;
                    var bar3Y = y + (isDcEarthEmblem ? 3.5 : 3.0);
                    var glyphThickness = isDcEarthEmblem ? 1.55 : 1.0;
                    c.Children.Add(new Line { StartPoint = new Point(glyphCenterX, y - stemTop), EndPoint = new Point(glyphCenterX, bar1Y), Stroke = p, StrokeThickness = glyphThickness });
                    c.Children.Add(new Line { StartPoint = new Point(glyphCenterX - halfWidth, bar1Y), EndPoint = new Point(glyphCenterX + halfWidth, bar1Y), Stroke = p, StrokeThickness = glyphThickness });
                    c.Children.Add(new Line { StartPoint = new Point(glyphCenterX - (isDcEarthEmblem ? 4.0 : 2.0), bar2Y), EndPoint = new Point(glyphCenterX + (isDcEarthEmblem ? 4.0 : 2.0), bar2Y), Stroke = p, StrokeThickness = glyphThickness });
                    c.Children.Add(new Line { StartPoint = new Point(glyphCenterX - (isDcEarthEmblem ? 2.25 : 1.0), bar3Y), EndPoint = new Point(glyphCenterX + (isDcEarthEmblem ? 2.25 : 1.0), bar3Y), Stroke = p, StrokeThickness = glyphThickness });
                }
                if (kind is not (ComponentKind.PowerEarth or ComponentKind.PowerDcEarth))
                    c.Children.Add(new Line { StartPoint = new Point(labels[i] == "PE" ? 19 : 10, y), EndPoint = new Point(30, y), Stroke = p, StrokeThickness = 1.4 });
            }
            if (kind is ComponentKind.PowerPhase or ComponentKind.PowerNeutral or ComponentKind.PowerEarth)
                c.RenderTransform = ScaleAbout(1.10, 1.10, 16, 13);
            SetSymbolStrokeThickness(c, 1.4);
            return c;
        }

        if (kind is ComponentKind.ConContact or ComponentKind.PushButtonCon or ComponentKind.ToggleSwitchCon)
        {
            AddLine(c, 16, 1, 16, 8, p, 1.7);
            AddLine(c, 16, 8, 10, 16, p, 1.7);
            AddLine(c, 6, 16, 6, 25, p, 1.7);
            AddLine(c, 26, 16, 26, 25, p, 1.7);
            AddLine(c, 6, 16, 10, 16, p, 1.7);
            AddLine(c, 22, 16, 26, 16, p, 1.7);
            if (kind == ComponentKind.PushButtonCon)
            {
                c.Children.Add(new Line { StartPoint = new Point(3, 12), EndPoint = new Point(13, 12), Stroke = p, StrokeThickness = 1.6, StrokeDashArray = new DoubleCollection { 1.5, 1.5 } });
                var eLabel = new TextBlock { Text = "E", Foreground = p, FontSize = 8, FontStyle = FontStyle.Italic };
                Canvas.SetLeft(eLabel, 0); Canvas.SetTop(eLabel, 6); c.Children.Add(eLabel);
            }
        }
        else if (kind == ComponentKind.PushButtonNO_NC)
        {
            var miniPair = new Canvas
            {
                Width = 100, Height = 64,
                RenderTransform = new ScaleTransform(0.46368, 0.46368),
                RenderTransformOrigin = RelativePoint.TopLeft,
                IsHitTestVisible = false
            };
            var miniNc = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(-26, 0) };
            DrawContactSymbol(miniNc, p, true, ComponentKind.PushButtonNC, "11", "12", drawActuator: false, contactThickness: 3.67, showTerminalNumbers: false);
            miniPair.Children.Add(miniNc);
            var miniNo = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(10, 0) };
            DrawContactSymbol(miniNo, p, false, ComponentKind.PushButtonNO, "13", "14", drawActuator: false, contactThickness: 3.67, showTerminalNumbers: false);
            miniPair.Children.Add(miniNo);
            miniPair.Children.Add(new Line { StartPoint = new Point(22, 28), EndPoint = new Point(58, 28), Stroke = p, StrokeThickness = 2.16, StrokeDashArray = new DoubleCollection { 3.0, 2.2 } });
            var eLabel = new TextBlock { Text = "E", Foreground = p, FontSize = 13, FontStyle = FontStyle.Italic };
            Canvas.SetLeft(eLabel, 12); Canvas.SetTop(eLabel, 18); miniPair.Children.Add(eLabel);
            Canvas.SetLeft(miniPair, -3); Canvas.SetTop(miniPair, -1.96); c.Children.Add(miniPair);
        }
        else if (kind == ComponentKind.ToggleSwitchNO_NC)
        {
            var miniPair = new Canvas { Width = 100, Height = 64, RenderTransform = new ScaleTransform(0.46368, 0.46368), RenderTransformOrigin = RelativePoint.TopLeft, IsHitTestVisible = false };
            var miniNc = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(-26, 0) };
            DrawContactSymbol(miniNc, p, true, ComponentKind.AuxContactNC, "1", "2", contactThickness: 3.67, showTerminalNumbers: false);
            miniPair.Children.Add(miniNc);
            var miniNo = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(10, 0) };
            DrawContactSymbol(miniNo, p, false, ComponentKind.AuxContactNO, "3", "4", contactThickness: 3.67, showTerminalNumbers: false);
            miniPair.Children.Add(miniNo);
            Canvas.SetLeft(miniPair, -3); Canvas.SetTop(miniPair, -1.96); c.Children.Add(miniPair);
        }
        else if (kind is ComponentKind.ToggleSwitchNO or ComponentKind.ToggleSwitchNC)
        {
            AddLine(c, 23, 1, 23, 8, p, 1.7);
            AddLine(c, 23, 16, 23, 25, p, 1.7);
            if (kind == ComponentKind.ToggleSwitchNC)
            {
                AddLine(c, 23, 8, 25.5, 8, p, 1.7);
                AddLine(c, 25.5, 8, 24.2, 11, p, 1.7);
                AddLine(c, 24.2, 11, 23, 16, p, 1.7);
            }
            else
            {
                AddLine(c, 18, 8, 23, 16, p, 1.7);
            }
        }
        else if (kind == ComponentKind.CrossoverSwitch)
        {
            foreach (var y in new[] { 7.0, 19.0 })
            {
                AddLine(c, 1, y, 8, y, p, 1.6);
                AddLine(c, 24, y, 31, y, p, 1.6);
            }
            AddLine(c, 8, 7, 24, 19, p, 1.6);
            AddLine(c, 8, 19, 24, 7, p, 1.6);
            AddLine(c, 16, 2, 16, 7, p, 1.6);
        }
        else if (kind == ComponentKind.ThermalRelayAuxNO_NC)
        {
            var miniRelayPair = new Canvas { Width = 100, Height = 64, RenderTransform = new ScaleTransform(0.46368, 0.46368), RenderTransformOrigin = RelativePoint.TopLeft, IsHitTestVisible = false };
            var miniNcRelay = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(-26, 0) };
            DrawContactSymbol(miniNcRelay, p, true, ComponentKind.AuxContactNC, "95", "96", showTerminalNumbers: false);
            miniRelayPair.Children.Add(miniNcRelay);
            var miniNoRelay = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(10, 0) };
            DrawContactSymbol(miniNoRelay, p, false, ComponentKind.AuxContactNO, "97", "98", showTerminalNumbers: false);
            miniRelayPair.Children.Add(miniNoRelay);
            DrawThermalRelayPairMark(miniRelayPair, p, 0);
            Canvas.SetLeft(miniRelayPair, -6); Canvas.SetTop(miniRelayPair, -1.96); c.Children.Add(miniRelayPair);
        }
        else if (kind is ComponentKind.OverloadNO or ComponentKind.ThermalRelayAuxNC)
        {
            AddLine(c, 23, 1, 23, 8, p, 1.7);
            AddLine(c, 23, 16, 23, 25, p, 1.7);
            if (kind == ComponentKind.ThermalRelayAuxNC)
            {
                AddLine(c, 23, 8, 25.5, 8, p, 1.7);
                AddLine(c, 25.5, 8, 24.2, 11, p, 1.7);
                AddLine(c, 24.2, 11, 23, 16, p, 1.7);
            }
            else AddLine(c, 18, 8, 23, 16, p, 1.7);
            AddLine(c, 9.5, 8, 9.5, 13, p, 1.7);
            AddLine(c, 9.5, 8, 14, 8, p, 1.7);
            AddLine(c, 14, 8, 14, 13, p, 1.7);
            c.Children.Add(new Line { StartPoint = new Point(14, 10.5), EndPoint = new Point(19.5, 10.5), Stroke = p, StrokeThickness = 1.6, StrokeDashArray = new DoubleCollection { 1.5, 1.5 } });
            c.Children.Add(new Rectangle { Width = 6, Height = 6, Stroke = p, StrokeThickness = 1.7, Fill = Brushes.Transparent });
            Canvas.SetLeft(c.Children[^1], 26); Canvas.SetTop(c.Children[^1], 8);
        }
        else if (kind is ComponentKind.PushButtonNO or ComponentKind.PushButtonNC
            or ComponentKind.LimitSwitchNO or ComponentKind.LimitSwitchNC // <--- поддержка концевиков
            or ComponentKind.AuxContactNO or ComponentKind.AuxContactNC
            or ComponentKind.ToggleSwitchNO or ComponentKind.ToggleSwitchNC)
        {
            var isButton = kind is ComponentKind.PushButtonNO or ComponentKind.PushButtonNC;
            var isLimitSwitch = kind is ComponentKind.LimitSwitchNO or ComponentKind.LimitSwitchNC;
            var isNormallyClosed = kind is ComponentKind.PushButtonNC or ComponentKind.AuxContactNC or ComponentKind.ToggleSwitchNC or ComponentKind.LimitSwitchNC;

            // Верхний неподвижный контакт
            c.Children.Add(new Line { StartPoint = new Point(23, 1), EndPoint = new Point(23, 8), Stroke = p, StrokeThickness = 1.7 });

            // Нижний вывод
            c.Children.Add(new Line { StartPoint = new Point(23, 16), EndPoint = new Point(23, 25), Stroke = p, StrokeThickness = 1.7 });

            if (isNormallyClosed)
            {
                c.Children.Add(new Line { StartPoint = new Point(23, 8), EndPoint = new Point(25.5, 8), Stroke = p, StrokeThickness = 1.7 });
                c.Children.Add(new Line { StartPoint = new Point(25.5, 8), EndPoint = new Point(24.2, 11), Stroke = p, StrokeThickness = 1.7 });
                c.Children.Add(new Line { StartPoint = new Point(24.2, 11), EndPoint = new Point(23, 16), Stroke = p, StrokeThickness = 1.7 });
            }
            else
            {
                c.Children.Add(new Line { StartPoint = new Point(18, 8), EndPoint = new Point(23, 16), Stroke = p, StrokeThickness = 1.7 });
            }

            // Обозначение обычной кнопки (буква E)
            if (isButton)
            {
                c.Children.Add(new Line { StartPoint = new Point(8, 11), EndPoint = new Point(20, 11), Stroke = p, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 1.5, 1.5 } });
                var eLabel = new TextBlock
                {
                    Text = "E",
                    Foreground = p,
                    FontSize = 8,
                    FontStyle = FontStyle.Italic
                };
                Canvas.SetLeft(eLabel, 3);
                Canvas.SetTop(eLabel, 6);
                c.Children.Add(eLabel);
            }

            // Обозначение концевика (кружок-ролик со штоком)
            if (isLimitSwitch)
            {
                c.Children.Add(new Line { StartPoint = new Point(11, 11), EndPoint = new Point(20, 11), Stroke = p, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 1.5, 1.5 } });
                c.Children.Add(new Ellipse { Width = 6, Height = 6, Stroke = p, StrokeThickness = 1.3, Fill = Brushes.Transparent });
                Canvas.SetLeft(c.Children[^1], 5);
                Canvas.SetTop(c.Children[^1], 8);
            }
        }
        else if (kind == ComponentKind.AuxContactNO_NC)
        {
            AddLine(c, 10, 3, 10, 10, p, 1.6);
            AddLine(c, 10, 16, 10, 24, p, 1.6);
            AddLine(c, 10, 10, 13, 10, p, 1.6);
            AddLine(c, 13, 10, 12, 13, p, 1.6);
            AddLine(c, 12, 13, 10, 16, p, 1.6);
            AddLine(c, 22, 3, 22, 10, p, 1.6);
            AddLine(c, 19, 10, 22, 16, p, 1.6);
            AddLine(c, 22, 16, 22, 24, p, 1.6);
        }
        else if (kind == ComponentKind.OverloadNC)
        {
            c.Children.Add(new Line { StartPoint = new Point(1, 14), EndPoint = new Point(9, 14), Stroke = p, StrokeThickness = 2 });
            c.Children.Add(new Line { StartPoint = new Point(9, 7), EndPoint = new Point(9, 21), Stroke = p, StrokeThickness = 2 });
            c.Children.Add(new Line { StartPoint = new Point(21, 7), EndPoint = new Point(21, 21), Stroke = p, StrokeThickness = 2 });
            c.Children.Add(new Line { StartPoint = new Point(21, 14), EndPoint = new Point(31, 14), Stroke = p, StrokeThickness = 2 });
            c.Children.Add(new Line { StartPoint = new Point(9, 7), EndPoint = new Point(21, 7), Stroke = p, StrokeThickness = 2 });
        }
        else if (kind == ComponentKind.DcMotor)
        {
            c.Children.Add(new Ellipse { Width = 27, Height = 27, Stroke = p, StrokeThickness = 1.5 });
            Canvas.SetLeft(c.Children[^1], 5); Canvas.SetTop(c.Children[^1], 2);
            c.Children.Add(new TextBlock { Text = "M", Foreground = p, FontSize = 10, FontWeight = FontWeight.SemiBold });
            Canvas.SetLeft(c.Children[^1], 13.5); Canvas.SetTop(c.Children[^1], 4);
            AddLine(c, 12, 18, 25, 18, p, 1.8);
            c.Children.Add(new Line { StartPoint = new Point(12, 21), EndPoint = new Point(25, 21), Stroke = p, StrokeThickness = 1.4, StrokeDashArray = new DoubleCollection { 2, 1.5 } });
        }
        else if (kind is ComponentKind.AcMotor or ComponentKind.AcMotor1P)
        {
            c.Children.Add(new Ellipse { Width = 27, Height = 27, Stroke = p, StrokeThickness = 1.5 });
            Canvas.SetLeft(c.Children[^1], 5); Canvas.SetTop(c.Children[^1], 2);
            c.Children.Add(new TextBlock { Text = "M", Foreground = p, FontSize = 10, FontWeight = FontWeight.SemiBold });
            Canvas.SetLeft(c.Children[^1], 13.5); Canvas.SetTop(c.Children[^1], 4);
            c.Children.Add(new TextBlock { Text = kind == ComponentKind.AcMotor ? "3" : "1", Foreground = p, FontSize = 6, FontWeight = FontWeight.Bold });
            Canvas.SetLeft(c.Children[^1], 8); Canvas.SetTop(c.Children[^1], 13);
            var wave = Geometry.Parse("M 12,18 C 16,15 20,21 26,18");
            c.Children.Add(new Avalonia.Controls.Shapes.Path { Data = wave, Stroke = p, StrokeThickness = 1.6, Fill = Brushes.Transparent });
        }
        else if (kind is ComponentKind.Coil or ComponentKind.AcSupply or ComponentKind.Lamp or ComponentKind.BlinkingLamp or ComponentKind.Motor)
        {
            if (kind == ComponentKind.Coil)
            {
                AddLine(c, 16, 0, 16, 5, p, 1.7);
                c.Children.Add(new Rectangle { Width = 22, Height = 14, Stroke = p, StrokeThickness = 1.7 });
                Canvas.SetLeft(c.Children[^1], 5); Canvas.SetTop(c.Children[^1], 5);
                AddLine(c, 25, 6, 7, 18, p, 1.4);
                AddLine(c, 16, 19, 16, 25, p, 1.7);
                SetSymbolStrokeThickness(c, 1.4);
                return c;
            }
            c.Children.Add(new Ellipse { Width = 22, Height = 22, Stroke = p, StrokeThickness = 2 });
            Canvas.SetLeft(c.Children[^1], 5); Canvas.SetTop(c.Children[^1], 2);
            if (kind is ComponentKind.Lamp or ComponentKind.BlinkingLamp)
            {
                AddLine(c, 11, 8, 21, 18, p, 2);
                AddLine(c, 21, 8, 11, 18, p, 2);
                AddLine(c, 16, 0, 16, 2, p, 1.4);
                AddLine(c, 16, 24, 16, 26, p, 1.4);
            }
            else
            {
                c.Children.Add(new TextBlock { Text = kind == ComponentKind.AcSupply ? "~" : kind == ComponentKind.Motor ? "M" : "K", Foreground = p, FontSize = 13 });
                Canvas.SetLeft(c.Children[^1], 11); Canvas.SetTop(c.Children[^1], 4);
            }
        }
        else if (kind is ComponentKind.Breaker1P or ComponentKind.Breaker1PN
            or ComponentKind.Breaker2P or ComponentKind.Breaker3P or ComponentKind.Breaker3PN)
        {
            if (kind == ComponentKind.Breaker3P)
            {
                foreach (var offset in new[] { -9.5, 5.0, 19.5 })
                {
                    var glyph = new Canvas { Width = 32, Height = 26, RenderTransform = ScaleAbout(0.903, 0.903, 16, 11), RenderTransformOrigin = RelativePoint.TopLeft };
                    AddLine(glyph, 16.5, -4, 16.5, 4, p, 1.4);
                    AddLine(glyph, 14, 2, 19, 9, p, 1.4);
                    AddLine(glyph, 19, 2, 14, 9, p, 1.4);
                    AddLine(glyph, 14, 10, 19, 10, p, 1.4);
                    AddLine(glyph, 7.81, 11.13, 16, 16, p, 1.4);
                    AddLine(glyph, 16, 15, 16, 26, p, 1.4);
                    Canvas.SetLeft(glyph, offset);
                    c.Children.Add(glyph);
                }
            }
            else if (kind == ComponentKind.Breaker3PN)
            {
                var offsets = new[] { -5.0, 7.5, 20.0, 32.5 };
                for (var pole = 0; pole < offsets.Length; pole++)
                {
                    var offset = offsets[pole];
                    var glyph = new Canvas { Width = 32, Height = 26, RenderTransform = ScaleAbout(0.86, 0.86, 16, 11), RenderTransformOrigin = RelativePoint.TopLeft };
                    if (pole == 3)
                    {
                        AddLine(glyph, 17, -2, 17, 8, p, 1.4);
                        AddLine(glyph, 8.81, 12.13, 17, 17, p, 1.4);
                        AddLine(glyph, 17, 17, 17, 26, p, 1.4);
                    }
                    else
                    {
                        AddLine(glyph, 16.5, -4, 16.5, 4, p, 1.4);
                        AddLine(glyph, 14, 2, 19, 9, p, 1.4);
                        AddLine(glyph, 19, 2, 14, 9, p, 1.4);
                        AddLine(glyph, 14, 10, 19, 10, p, 1.4);
                        AddLine(glyph, 7.81, 11.13, 16, 16, p, 1.4);
                        AddLine(glyph, 16, 15, 16, 26, p, 1.4);
                    }
                    Canvas.SetLeft(glyph, offset);
                    c.Children.Add(glyph);
                }
            }
            else
            {
                // Thermal-magnetic breaker emblem, based on the supplied reference.
                var breakerGlyphOffset = kind == ComponentKind.Breaker2P
                    ? -6.5
                    : kind == ComponentKind.Breaker1PN ? -5.0 : 0.0;
                AddLine(c, 16.5 + breakerGlyphOffset, -4, 16.5 + breakerGlyphOffset, 4, p, 1.4);
                AddLine(c, 14 + breakerGlyphOffset, 2, 19 + breakerGlyphOffset, 9, p, 1.4);
                AddLine(c, 19 + breakerGlyphOffset, 2, 14 + breakerGlyphOffset, 9, p, 1.4);
                AddLine(c, 14 + breakerGlyphOffset, 10, 19 + breakerGlyphOffset, 10, p, 1.4);
                AddLine(c, 8.6 + breakerGlyphOffset, 11.6, 16 + breakerGlyphOffset, 16, p, 1.4);
                AddLine(c, 16 + breakerGlyphOffset, 15, 16 + breakerGlyphOffset, 26, p, 1.4);
                if (kind == ComponentKind.Breaker2P)
                {
                    const double copyOffset = 15;
                    AddLine(c, 16.5 + breakerGlyphOffset + copyOffset, -4, 16.5 + breakerGlyphOffset + copyOffset, 4, p, 1.4);
                    AddLine(c, 14 + breakerGlyphOffset + copyOffset, 2, 19 + breakerGlyphOffset + copyOffset, 9, p, 1.4);
                    AddLine(c, 19 + breakerGlyphOffset + copyOffset, 2, 14 + breakerGlyphOffset + copyOffset, 9, p, 1.4);
                    AddLine(c, 14 + breakerGlyphOffset + copyOffset, 10, 19 + breakerGlyphOffset + copyOffset, 10, p, 1.4);
                    AddLine(c, 8.6 + breakerGlyphOffset + copyOffset, 11.6, 16 + breakerGlyphOffset + copyOffset, 16, p, 1.4);
                    AddLine(c, 16 + breakerGlyphOffset + copyOffset, 15, 16 + breakerGlyphOffset + copyOffset, 26, p, 1.4);
                }
                if (kind == ComponentKind.Breaker1PN)
                {
                    // Open contact mark shown on the second item in the breaker category.
                    AddLine(c, 28, -2, 28, 8, p, 1.4);
                    AddLine(c, 19.5, 11.6, 28, 17, p, 1.4);
                    AddLine(c, 28, 17, 28, 26, p, 1.4);
                }
            }
        }
        else if (kind is ComponentKind.Rcd1PN or ComponentKind.Rcd3PN)
        {
            AddLine(c, 16, 0, 16, 6, p, 1.4);
            AddLine(c, 10, 6, 16, 10, p, 1.4);
            AddLine(c, 16, 10, 16, 14, p, 1.4);
            c.Children.Add(new Polygon
            {
                Points = new PointCollection { new(8, 6), new(10, 4), new(12, 6), new(10, 8) },
                Fill = p, Stroke = p, StrokeThickness = 1.4
            });
            c.Children.Add(new Rectangle { Width = 22, Height = 8, Stroke = p, StrokeThickness = 1.4 });
            Canvas.SetLeft(c.Children[^1], 5); Canvas.SetTop(c.Children[^1], 14);
            AddLine(c, 16, 22, 16, 26, p, 1.4);
        }
        else if (kind == ComponentKind.ThermalRelay)
        {
            c.Children.Add(new Rectangle { Width = 28, Height = 12, Stroke = p, StrokeThickness = 1.4 });
            Canvas.SetLeft(c.Children[^1], 2); Canvas.SetTop(c.Children[^1], 6);
            AddLine(c, 16, 0, 16, 6, p, 1.4);
            AddLine(c, 16, 18, 16, 26, p, 1.4);
            c.Children.Add(new Polyline
            {
                Points = new PointCollection { new(16, 6), new(16, 9), new(23, 9), new(23, 15), new(16, 15), new(16, 18) },
                Stroke = p, StrokeThickness = 1.7
            });
        }
        else if (kind is ComponentKind.Contactor2P or ComponentKind.Contactor3P or ComponentKind.Contactor4P)
        {
            var poles = kind == ComponentKind.Contactor2P ? 2 : kind == ComponentKind.Contactor3P ? 3 : 4;
            var spacing = poles == 2 ? 12.0 : poles == 3 ? 9.0 : 7.0;
            var firstX = 16 - spacing * (poles - 1) / 2;
            for (var pole = 0; pole < poles; pole++)
            {
                var x = firstX + spacing * pole;
                AddLine(c, x, 2, x, 8, p, 1.5);
                AddLine(c, x - 3.5, 8, x, 15, p, 1.5);
                AddLine(c, x, 15, x, 24, p, 1.5);
            }
        }
        else if (kind is ComponentKind.CircuitBreaker or ComponentKind.DifferentialBreaker)
        {
            c.Children.Add(new Rectangle { Width = 17, Height = 21, Stroke = p, StrokeThickness = 2 });
            Canvas.SetLeft(c.Children[^1], 8); Canvas.SetTop(c.Children[^1], 2);
            if (kind == ComponentKind.DifferentialBreaker)
            {
                c.Children.Add(new TextBlock { Text = "Δ", Foreground = p, FontSize = 12 });
                Canvas.SetLeft(c.Children[^1], 11); Canvas.SetTop(c.Children[^1], 4);
            }
        }
        else
        {
            c.Children.Add(new Line { StartPoint = new Point(10, 2), EndPoint = new Point(10, 24), Stroke = p, StrokeThickness = 3 });
            c.Children.Add(new Line { StartPoint = new Point(22, 6), EndPoint = new Point(22, 20), Stroke = p, StrokeThickness = 2 });
        }
        SetSymbolStrokeThickness(c, 1.4);
        return c;
    }

    private static void SetSymbolStrokeThickness(Panel panel, double thickness)
    {
        foreach (var child in panel.Children)
        {
            if (child is Shape shape)
                shape.StrokeThickness = thickness;
            else if (child is Panel nestedPanel)
                SetSymbolStrokeThickness(nestedPanel, thickness);
        }
    }

    private static void SetSymbolStrokeThicknessForDisplay(Panel panel, double thickness, double parentScale = 1)
    {
        var localScale = 1.0;
        if (panel.RenderTransform is Transform transform)
        {
            var matrix = transform.Value;
            var scaleX = Math.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12);
            var scaleY = Math.Sqrt(matrix.M21 * matrix.M21 + matrix.M22 * matrix.M22);
            localScale = Math.Max(0.001, (scaleX + scaleY) / 2);
        }

        var displayScale = parentScale * localScale;
        foreach (var child in panel.Children)
        {
            if (child is Shape shape)
                shape.StrokeThickness = thickness / displayScale;
            else if (child is Panel nestedPanel)
                SetSymbolStrokeThicknessForDisplay(nestedPanel, thickness, displayScale);
        }
    }

    private static void AddMiniTogglePole(Canvas canvas, IBrush stroke, double centerX, bool closed, double terminalHalfSpan)
    {
        const double contactY = 14;
        var contactOffset = terminalHalfSpan / 2;
        var leftTerminal = centerX - terminalHalfSpan;
        var rightTerminal = centerX + terminalHalfSpan;
        var leftContact = centerX - contactOffset;
        var rightContact = centerX + contactOffset;
        AddLine(canvas, leftTerminal, contactY, leftContact - 1.5, contactY, stroke, 1.4);
        AddLine(canvas, rightContact + 1.5, contactY, rightTerminal, contactY, stroke, 1.4);
        foreach (var x in new[] { leftContact, rightContact })
        {
            canvas.Children.Add(new Ellipse { Width = 3, Height = 3, Stroke = stroke, StrokeThickness = 1.2, Fill = Brushes.Transparent });
            Canvas.SetLeft(canvas.Children[^1], x - 1.5);
            Canvas.SetTop(canvas.Children[^1], contactY - 1.5);
        }
        var bridgeY = closed ? 15.5 : 7.0;
        AddLine(canvas, leftContact, bridgeY, rightContact, bridgeY, stroke, 1.4);
        AddLine(canvas, centerX, 1, centerX, bridgeY, stroke, 1.4);
    }

    private static readonly Dictionary<string, IBrush> _brushCache = new();
    private static IBrush Brush(string hex)
    {
        if (!_brushCache.TryGetValue(hex, out var brush))
            _brushCache[hex] = brush = SolidColorBrush.Parse(hex);
        return brush;
    }
    private string CurrentWireColorLabel() => WireColors.FirstOrDefault(c => c.Key == _selectedWireColorKey).Label ?? _selectedWireColorKey;
    private string WireColorHex(string key) => WireColors.FirstOrDefault(c => c.Key == key).Hex ?? "#8B4513";

    private void NewProject()
    {
        SaveCameraState();
        _currentFilePath = null;
        RestoreCameraState(null);
        _undoStack.Clear();
        _dragUndoSaved = false;
        _project = new ProjectDocument { Name = "Untitled", SchemaVersion = 4 };
        _selected = null; _selectedComponentIds.Clear(); _selectedWireId = null;
        _wireStart = null; _wireWaypoints.Clear(); _hoverTerminal = null;
        _placingKind = null; _wireMode = false; _dragging = false; _selectingArea = false;
        _dragOrigins.Clear(); _wireDragOrigins.Clear(); _pressedButtonId = null;
        _simulator.Stop();
        UpdateSimulationButtons();
        Redraw();
        StatusText.Text = "Готово. Добавь источник, компоненты и соедини их терминалы проводами.";
    }

    private void Canvas_MiddleMouseDown(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(Viewport).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonPressed) return;
        if (e.ClickCount >= 2)
        {
            _panning = false;
            ReleaseCapture();
            FitSchematicToViewport();
            e.Handled = true;
            return;
        }
        _panning = true;
        _panLastPoint = e.GetPosition(Viewport);
        _pointer = e.Pointer;
        e.Pointer.Capture(SchematicCanvas);
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Handled = true;
    }

    private void Canvas_MiddleMouseMove(object? sender, PointerEventArgs e)
    {
        if (!_panning || !e.GetCurrentPoint(Viewport).Properties.IsMiddleButtonPressed) return;
        var current = e.GetPosition(Viewport);
        _panTransform.X += current.X - _panLastPoint.X;
        _panTransform.Y += current.Y - _panLastPoint.Y;
        _panLastPoint = current;
        e.Handled = true;
    }

    private void Canvas_MiddleMouseUp(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle || !_panning) return;
        _panning = false;
        _pointer = e.Pointer;
        ReleaseCapture();
        Cursor = new Cursor(StandardCursorType.Arrow);
        SaveCameraState();
        e.Handled = true;
    }

    private void Canvas_MouseWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0) return;
        var oldZoom = _zoom;
        var p = e.GetPosition(Viewport);
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1), 0.35, 3.0);
        var ratio = _zoom / oldZoom;

        // Keep the point under the cursor stationary while scaling.
        _panTransform.X = p.X - (p.X - _panTransform.X) * ratio;
        _panTransform.Y = p.Y - (p.Y - _panTransform.Y) * ratio;
        _zoomTransform.ScaleX = _zoom;
        _zoomTransform.ScaleY = _zoom;
        Redraw();
        SaveCameraState();

        _showingZoomStatus = true;
        StatusText.Text = $"{L("Zoom_Status")} {Math.Round(_zoom * 100)}%.";
        e.Handled = true;
    }

    // Mouse coordinates from the transformed Canvas can vary with its render transform.
    // Convert viewport coordinates explicitly so zoom and drag use the same world space.
    private Point GetWorldPosition(PointerEventArgs e)
    {
        var point = e.GetPosition(Viewport);
        return new Point((point.X - _panTransform.X) / _zoom, (point.Y - _panTransform.Y) / _zoom);
    }

    private void Canvas_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = !_simulator.IsRunning && _dragInFlight is not null
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Canvas_Drop(object? sender, DragEventArgs e)
    {
        var item = _dragInFlight;
        if (_simulator.IsRunning || item is null || item.Kind is not ComponentKind kind)
            return;

        var point = e.GetPosition(Viewport);
        var worldPosition = new Point(
            (point.X - _panTransform.X) / _zoom,
            (point.Y - _panTransform.Y) / _zoom);
        PushUndoState();
        var component = new ComponentModel
        {
            Kind = kind,
            Tag = GetPlacementTag(kind, item.DefaultTag),
            X = Snap(worldPosition.X),
            Y = Snap(worldPosition.Y),
            Rotation = ComponentTerminals.IsPowerMarker(kind) ? 90 : 0,
            Phases = 1,
            Voltage = 230
        };
        _project.Components.Add(component);
        _placingKind = null;
        _wireMode = false;
        _wireStart = null;
        _wireWaypoints.Clear();
        _selected = component;
        _selectedComponentIds.Clear();
        _selectedComponentIds.Add(component.Id);
        _selectedWireId = null;
        RefreshSelectedStatus();
        Redraw();
        StatusText.Text = $"Размещён {component.Tag}.";
        e.Handled = true;
    }

    private Guid? HitWire(Point point)
    {
        var tolerance = Math.Clamp(7 / _zoom, 3, 14);
        foreach (var wire in _project.Wires.AsEnumerable().Reverse())
        {
            var aComp = _project.Components.FirstOrDefault(c => c.Id == wire.StartComponentId);
            var bComp = _project.Components.FirstOrDefault(c => c.Id == wire.EndComponentId);
            if (aComp is null || bComp is null) continue;

            var a = TerminalPosition(aComp, wire.StartTerminalId);
            var b = TerminalPosition(bComp, wire.EndTerminalId);
            var path = BuildWirePath(a, b, wire.Waypoints?.Select(p => new Point(p.X, p.Y)));
            for (var i = 0; i < path.Count - 1; i++)
                if (DistanceToSegment(point, path[i], path[i + 1]) <= tolerance)
                    return wire.Id;
        }
        return null;
    }

    private (Guid WireId, int Segment)? HitWireSegment(Point point)
    {
        var tolerance = Math.Clamp(9 / _zoom, 4, 16);
        foreach (var wire in _project.Wires.AsEnumerable().Reverse())
        {
            var aComp = _project.Components.FirstOrDefault(c => c.Id == wire.StartComponentId);
            var bComp = _project.Components.FirstOrDefault(c => c.Id == wire.EndComponentId);
            if (aComp is null || bComp is null) continue;
            var path = BuildWirePath(TerminalPosition(aComp, wire.StartTerminalId), TerminalPosition(bComp, wire.EndTerminalId), wire.Waypoints?.Select(p => new Point(p.X, p.Y)));
            for (var i = 0; i < path.Count - 1; i++)
                if (DistanceToSegment(point, path[i], path[i + 1]) <= tolerance) return (wire.Id, i);
        }
        return null;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        if (dx == 0 && dy == 0) return Len(p - a);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
        return Len(p - new Point(a.X + t * dx, a.Y + t * dy));
    }

    private void ConnectWireToWire(Guid targetWireId, Point cursor)
    {
        if (_wireStart is not TerminalRef source) return;
        var targetWire = _project.Wires.FirstOrDefault(w => w.Id == targetWireId);
        if (targetWire is null) return;
        var start = _project.Components.FirstOrDefault(c => c.Id == targetWire.StartComponentId);
        var end = _project.Components.FirstOrDefault(c => c.Id == targetWire.EndComponentId);
        var activeStart = _project.Components.FirstOrDefault(c => c.Id == source.ComponentId);
        if (start is null || end is null || activeStart is null) return;

        var terminalAlreadyWired = _project.Wires.Any(w =>
            (w.StartComponentId == source.ComponentId && w.StartTerminalId == source.TerminalId) ||
            (w.EndComponentId == source.ComponentId && w.EndTerminalId == source.TerminalId));
        if (terminalAlreadyWired)
        {
            var targetTerminal = (TerminalPosition(start, targetWire.StartTerminalId), start.Id, targetWire.StartTerminalId);
            var otherTerminal = (TerminalPosition(end, targetWire.EndTerminalId), end.Id, targetWire.EndTerminalId);
            var chosen = Len(cursor - targetTerminal.Item1) <= Len(cursor - otherTerminal.Item1) ? targetTerminal : otherTerminal;
            var pathToTerminal = BuildWirePath(TerminalPosition(activeStart, source.TerminalId), chosen.Item1, _wireWaypoints);
            var waypointsToTerminal = pathToTerminal.Skip(1).Take(Math.Max(0, pathToTerminal.Count - 2))
                .Select(p => new WireWaypoint { X = p.X, Y = p.Y }).ToList();
            PushUndoState();
            _project.Wires.Add(new WireModel
            {
                StartComponentId = source.ComponentId,
                StartTerminalId = source.TerminalId,
                EndComponentId = chosen.Item2,
                EndTerminalId = chosen.Item3,
                ColorKey = _selectedWireColorKey,
                Waypoints = waypointsToTerminal
            });
            _wireStart = null;
            _wireWaypoints.Clear();
            _wireMode = false;
            StatusText.Text = "Провод подключён к клемме существующего кабеля без создания узла.";
            return;
        }

        var originalPath = BuildWirePath(TerminalPosition(start, targetWire.StartTerminalId),
            TerminalPosition(end, targetWire.EndTerminalId), targetWire.Waypoints?.Select(p => new Point(p.X, p.Y)));
        var segmentIndex = 0;
        var nearest = originalPath[0];
        var bestDistance = double.MaxValue;
        for (var i = 0; i < originalPath.Count - 1; i++)
        {
            var a = originalPath[i];
            var b = originalPath[i + 1];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            var t = lengthSquared < 1e-9 ? 0 : Math.Clamp(((cursor.X - a.X) * dx + (cursor.Y - a.Y) * dy) / lengthSquared, 0, 1);
            var point = new Point(a.X + t * dx, a.Y + t * dy);
            var distance = Len(cursor - point);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            segmentIndex = i;
            nearest = Math.Abs(dy) < 0.5
                ? new Point(Math.Clamp(Snap(cursor.X), Math.Min(a.X, b.X), Math.Max(a.X, b.X)), a.Y)
                : new Point(a.X, Math.Clamp(Snap(cursor.Y), Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)));
        }

        var junction = new ComponentModel { Kind = ComponentKind.WireJunction, Tag = "", X = nearest.X, Y = nearest.Y };
        var firstWaypoints = originalPath.Skip(1).Take(segmentIndex)
            .Select(p => new WireWaypoint { X = p.X, Y = p.Y }).ToList();
        var secondWaypoints = originalPath.Skip(segmentIndex + 1).Take(originalPath.Count - segmentIndex - 2)
            .Select(p => new WireWaypoint { X = p.X, Y = p.Y }).ToList();
        var activePath = BuildWirePath(TerminalPosition(activeStart, source.TerminalId), nearest, _wireWaypoints);
        var activeWaypoints = activePath.Skip(1).Take(Math.Max(0, activePath.Count - 2))
            .Select(p => new WireWaypoint { X = p.X, Y = p.Y }).ToList();

        PushUndoState();
        var originalEndId = targetWire.EndComponentId;
        var originalEndTerminal = targetWire.EndTerminalId;
        targetWire.EndComponentId = junction.Id;
        targetWire.EndTerminalId = "J";
        targetWire.Waypoints = firstWaypoints;
        _project.Components.Add(junction);
        _project.Wires.Add(new WireModel
        {
            StartComponentId = junction.Id,
            StartTerminalId = "J",
            EndComponentId = originalEndId,
            EndTerminalId = originalEndTerminal,
            ColorKey = targetWire.ColorKey,
            Waypoints = secondWaypoints
        });
        _project.Wires.Add(new WireModel
        {
            StartComponentId = source.ComponentId,
            StartTerminalId = source.TerminalId,
            EndComponentId = junction.Id,
            EndTerminalId = "J",
            ColorKey = _selectedWireColorKey,
            Waypoints = activeWaypoints
        });
        _wireStart = null;
        _wireWaypoints.Clear();
        _wireMode = false;
        StatusText.Text = "Провода соединены узлом.";
    }

    private void Canvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pointer = e.Pointer;
        var kind = e.GetCurrentPoint(SchematicCanvas).Properties.PointerUpdateKind;
        if (kind == PointerUpdateKind.LeftButtonPressed) Canvas_LeftDown(e);
        else if (kind == PointerUpdateKind.RightButtonPressed) Canvas_RightDown(e);
    }

    private void Canvas_LeftDown(PointerPressedEventArgs e)
    {
        SchematicCanvas.Focus();
        _cursorPosition = GetWorldPosition(e);
        if (!_simulator.IsRunning && e.ClickCount == 2 && HitComponent(_cursorPosition) is { } breaker &&
            breaker.Kind is ComponentKind.Coil or ComponentKind.Lamp or ComponentKind.BlinkingLamp or ComponentKind.Breaker1P or ComponentKind.Breaker1PN or ComponentKind.Breaker2P or ComponentKind.Breaker3P or ComponentKind.Breaker3PN or ComponentKind.Rcd1PN or ComponentKind.Rcd3PN or ComponentKind.ThermalRelay or ComponentKind.Contactor2P or ComponentKind.Contactor3P or ComponentKind.Contactor4P or ComponentKind.PushButtonNO or ComponentKind.PushButtonNC or ComponentKind.PushButtonNO_NC or ComponentKind.PushButtonCon or ComponentKind.ToggleSwitchNO or ComponentKind.ToggleSwitchNC or ComponentKind.ToggleSwitchNO_NC or ComponentKind.ToggleSwitchCon or ComponentKind.CrossoverSwitch or ComponentKind.AuxContactNO or ComponentKind.AuxContactNC or ComponentKind.AuxContactNO_NC or ComponentKind.ConContact or ComponentKind.OverloadNC or ComponentKind.OverloadNO or ComponentKind.ThermalRelayAuxNC or ComponentKind.ThermalRelayAuxNO_NC or ComponentKind.DcMotor or ComponentKind.AcMotor or ComponentKind.AcMotor1P)
        {
            EditComponentTag(breaker);
            e.Handled = true;
            return;
        }

        // Wire mode: terminal click completes the wire, blank-space click locks a bend.
        if (_wireMode)
        {
            var terminal = FindNearestTerminal(_cursorPosition, Math.Clamp(8 / _zoom, 3.5, 12));
            if (terminal is null)
            {
                if (_wireStart is null)
                {
                    StatusText.Text = "Сначала выбери начальную клемму.";
                    return;
                }

                if (HitWire(_cursorPosition) is Guid targetWireId)
                {
                    ConnectWireToWire(targetWireId, _cursorPosition);
                    Redraw();
                    return;
                }

                LockWireCorner(_cursorPosition);
                StatusText.Text = $"Угол провода зафиксирован. Продолжай вести провод курсором или выбери конечную клемму.";
                Redraw();
                return;
            }

            if (_wireStart is null)
            {
                _wireWaypoints.Clear();
                _wireStart = terminal;
                var startComponent = _project.Components.First(c => c.Id == terminal.Value.ComponentId);
                StatusText.Text = $"Начало выбрано: {startComponent.Tag}.{TerminalLabel(startComponent, terminal.Value.TerminalId)}. Веди провод курсором, щёлкай по пустому месту для изгибов, затем выбери конечную клемму.";
            }
            else if (_wireStart.Value.ComponentId == terminal.Value.ComponentId &&
                     _wireStart.Value.TerminalId == terminal.Value.TerminalId)
            {
                StatusText.Text = "Выбери другую клемму.";
                return;
            }
            else
            {
                var startComponent = _project.Components.FirstOrDefault(c => c.Id == _wireStart.Value.ComponentId);
                var endComponent = _project.Components.FirstOrDefault(c => c.Id == terminal.Value.ComponentId);
                if (startComponent is null || endComponent is null)
                {
                    CancelTool();
                    return;
                }

                var wireStartPoint = TerminalPosition(startComponent, _wireStart.Value.TerminalId);
                var wireEndPoint = TerminalPosition(endComponent, terminal.Value.TerminalId);
                var finalPath = BuildWirePath(wireStartPoint, wireEndPoint, _wireWaypoints);
                var storedWaypoints = finalPath.Skip(1).Take(Math.Max(0, finalPath.Count - 2))
                    .Select(p => new WireWaypoint { X = p.X, Y = p.Y }).ToList();

                PushUndoState();
                _project.Wires.Add(new WireModel
                {
                    StartComponentId = _wireStart.Value.ComponentId,
                    StartTerminalId = _wireStart.Value.TerminalId,
                    EndComponentId = terminal.Value.ComponentId,
                    EndTerminalId = terminal.Value.TerminalId,
                    ColorKey = _selectedWireColorKey,
                    Waypoints = storedWaypoints
                });

                _wireStart = null;
                _wireWaypoints.Clear();
                _wireMode = false;
                StatusText.Text = $"Провод добавлен ({CurrentWireColorLabel().ToLowerInvariant()}).";
            }
            Redraw();
            return;
        }

        // Clicking a terminal starts a wire even when the wire tool was not selected first.
        var terminalHit = FindNearestTerminal(_cursorPosition, Math.Clamp(8 / _zoom, 3.5, 12));
        if (_placingKind is null && !_simulator.IsRunning && terminalHit is not null)
        {
            _terminalPressPosition = _cursorPosition;
            HandleTerminalBatchClick((terminalHit.Value.ComponentId, terminalHit.Value.TerminalId));
            Redraw();
            return;
        }

        if (_placingKind is not null)
        {
            var kind = _placingKind.Value;
            var item = _palette.First(x => x.Kind == kind);
            var c = new ComponentModel
            {
                Kind = kind, Tag = GetPlacementTag(kind, item.DefaultTag),
                X = Snap(_cursorPosition.X), Y = Snap(_cursorPosition.Y),
                Rotation = _placingRotation,
                Phases = kind == ComponentKind.AcSupply ? _pendingAcPhases : 1,
                Voltage = kind == ComponentKind.AcSupply ? _pendingAcVoltage : 230
            };
            PushUndoState();
            _project.Components.Add(c);
            _selected = c;
            _selectedComponentIds.Clear();
            _selectedComponentIds.Add(c.Id);
            RefreshSelectedStatus();
            Redraw();
            StatusText.Text = $"Размещён {c.Tag}. Инструмент остаётся активным.";
            return;
        }

        var wireSegmentHit = HitWireSegment(_cursorPosition);
        if (!_simulator.IsRunning && wireSegmentHit is { } segmentHit)
        {
            _selectedWireId = segmentHit.WireId;
            _selected = null;
            _selectedComponentIds.Clear();
            var wire = _project.Wires.First(w => w.Id == segmentHit.WireId);
            var start = _project.Components.First(c => c.Id == wire.StartComponentId);
            var end = _project.Components.First(c => c.Id == wire.EndComponentId);
            _wireMoveOriginalPath = BuildWirePath(TerminalPosition(start, wire.StartTerminalId), TerminalPosition(end, wire.EndTerminalId), wire.Waypoints?.Select(p => new Point(p.X, p.Y)));
            _movingWireId = wire.Id; _movingWireSegment = segmentHit.Segment; _wireMoveStart = _cursorPosition;
            CaptureCanvas();
            StatusText.Text = "Тяни участок кабеля, чтобы изменить его маршрут. Delete или Backspace удаляет кабель.";
            Redraw();
            return;
        }

        var hit = HitComponent(_cursorPosition);
        if (_simulator.IsRunning)
        {
            _selected = hit;
            if (hit is null) return;
            InteractWithComponent(hit);
            return;
        }

        _selectedWireId = null;
        if (hit is null)
        {
            _selected = null;
            _selectedComponentIds.Clear();
            _selectingArea = true;
            _selectionStart = _cursorPosition;
            _selectionCurrent = _cursorPosition;
            _dragOrigins.Clear();
            CaptureCanvas();
            Redraw();
            return;
        }

        var shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (shiftHeld)
        {
            if (!_selectedComponentIds.Add(hit.Id))
            {
                _selectedComponentIds.Remove(hit.Id);
                _selected = _project.Components.LastOrDefault(c => _selectedComponentIds.Contains(c.Id));
                RefreshSelectedStatus();
                Redraw();
                return;
            }
        }
        else if (!_selectedComponentIds.Contains(hit.Id))
        {
            _selectedComponentIds.Clear();
            _selectedComponentIds.Add(hit.Id);
        }
        _selected = hit;
        SchematicCanvas.Focus();
        _dragStart = _cursorPosition;
        _dragOrigins.Clear();
        foreach (var selectedComponent in _project.Components.Where(c => _selectedComponentIds.Contains(c.Id)))
            _dragOrigins[selectedComponent.Id] = new Point(selectedComponent.X, selectedComponent.Y);

        _wireDragOrigins.Clear();
        foreach (var wire in _project.Wires.Where(w =>
            _selectedComponentIds.Contains(w.StartComponentId) ||
            _selectedComponentIds.Contains(w.EndComponentId)))
        {
            _wireDragOrigins[wire.Id] = (wire.Waypoints ?? new List<WireWaypoint>())
                .Select(p => new Point(p.X, p.Y)).ToList();
        }

        _dragging = true;
        _dragUndoSaved = false;
        CaptureCanvas();
        RefreshSelectedStatus();
        Redraw();
    }

    private async void EditComponentTag(ComponentModel component)
    {
        var editor = new Window
        {
            Title = "Изменить обозначение", Width = 300, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        var input = new TextBox { Text = component.Tag, Margin = new Thickness(0, 0, 0, 10), MinWidth = 250 };
        var save = new Button { Content = "Сохранить", IsDefault = true, Width = 90, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) { PushUndoState(); component.Tag = input.Text.Trim(); Redraw(); } editor.Close(true); };
        panel.Children.Add(new TextBlock { Text = "Введите позиционное обозначение:" }); panel.Children.Add(input); panel.Children.Add(save); editor.Content = panel;
        editor.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        await editor.ShowDialog(this);
    }

    private void LockWireCorner(Point cursor)
    {
        if (_wireStart is null) return;
        var startComponent = _project.Components.FirstOrDefault(c => c.Id == _wireStart.Value.ComponentId);
        if (startComponent is null) return;

        var anchor = _wireWaypoints.Count > 0
            ? new Point(_wireWaypoints[^1].X, _wireWaypoints[^1].Y)
            : TerminalPosition(startComponent, _wireStart.Value.TerminalId);
        var target = new Point(Snap(cursor.X), Snap(cursor.Y));
        var segment = BuildWirePath(anchor, target, null);
        foreach (var point in segment.Skip(1))
        {
            if (_wireWaypoints.Count == 0 ||
                Math.Abs(_wireWaypoints[^1].X - point.X) > 0.5 ||
                Math.Abs(_wireWaypoints[^1].Y - point.Y) > 0.5)
            {
                _wireWaypoints.Add(point);
            }
        }
    }

    private void InteractWithComponent(ComponentModel c)
    {
        switch (c.Kind)
        {   case ComponentKind.LimitSwitchNO:
            case ComponentKind.LimitSwitchNC:
                _simulator.ToggleContactSwitch(c.Id, _project);
                StatusText.Text = $"{c.Tag}: {_simulator.GetState(c.Id)}";
                break;
            case ComponentKind.PushButtonNO:
            case ComponentKind.PushButtonNC:
            case ComponentKind.PushButtonNO_NC:
            case ComponentKind.PushButtonCon:
                _pressedButtonId = c.Id;
                _simulator.SetButtonsByTag(c.Id, true, _project);
                CaptureCanvas();
                StatusText.Text = $"{c.Tag}: кнопка нажата. Отпусти мышь, чтобы отпустить кнопку.";
                break;
            case ComponentKind.ToggleSwitchNO:
            case ComponentKind.ToggleSwitchNC:
            case ComponentKind.ToggleSwitchNO_NC:
            case ComponentKind.ToggleSwitchCon:
            case ComponentKind.CrossoverSwitch:
                _simulator.ToggleContactSwitch(c.Id, _project);
                StatusText.Text = $"{c.Tag}: {_simulator.GetState(c.Id)}";
                break;
            case ComponentKind.CircuitBreaker:
            case ComponentKind.DifferentialBreaker:
            case ComponentKind.Breaker1P: case ComponentKind.Breaker1PN: case ComponentKind.Breaker2P:
            case ComponentKind.Breaker3P: case ComponentKind.Breaker3PN: case ComponentKind.Rcd1PN:
            case ComponentKind.Rcd3PN:
                _simulator.ToggleProtectiveGroup(c.Id, _project);
                StatusText.Text = $"{c.Tag}: {_simulator.GetState(c.Id)}";
                break;
            case ComponentKind.OverloadNC:
                _simulator.ToggleOverloadGroup(c.Id, _project);
                StatusText.Text = $"{c.Tag}: {_simulator.GetState(c.Id)}";
                break;
            case ComponentKind.ThermalRelay:
                _simulator.ToggleThermalRelayGroup(c.Id, _project);
                StatusText.Text = $"{c.Tag}: {_simulator.GetState(c.Id)}";
                break;
            case ComponentKind.AcSupply:
            case ComponentKind.Supply24V:
                _simulator.ToggleSource(c.Id, _project);
                StatusText.Text = $"{c.Tag}: {_simulator.GetState(c.Id)}";
                break;
            case ComponentKind.AuxContactNO:
            case ComponentKind.AuxContactNC:
                StatusText.Text = $"{c.Tag}: {_simulator.GetState(c.Id)}. Состояние задаётся управляющим элементом с таким же обозначением.";
                break;
            case ComponentKind.Coil:
            case ComponentKind.Lamp:
            case ComponentKind.Motor:
            case ComponentKind.DcMotor:
            case ComponentKind.AcMotor:
            case ComponentKind.AcMotor1P:
                StatusText.Text = $"{c.Tag}: {_simulator.GetState(c.Id)}. Состояние определяется схемой.";
                break;
            default:
                StatusText.Text = $"{c.Tag}: {_simulator.GetState(c.Id)}";
                break;
        }
        Redraw();
    }

    private void Canvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        _pointer = e.Pointer;
        _cursorPosition = GetWorldPosition(e);

        if (!_simulator.IsRunning && IsLeft(e) && _movingWireId is Guid movingWire && _wireMoveOriginalPath is { Count: > 1 } originalPath)
        {
            if (!_wireMoveUndoSaved && Len(_cursorPosition - _wireMoveStart) > 1)
            {
                PushUndoState();
                _wireMoveUndoSaved = true;
            }
            var route = originalPath.ToList();
            var i = Math.Clamp(_movingWireSegment, 0, route.Count - 2);
            var a = originalPath[i]; var b = originalPath[i + 1];
            var horizontal = Math.Abs(a.Y - b.Y) <= Math.Abs(a.X - b.X);
            var coordinate = Snap(horizontal ? originalPath[i].Y + _cursorPosition.Y - _wireMoveStart.Y : originalPath[i].X + _cursorPosition.X - _wireMoveStart.X);
            if (route.Count == 2)
            {
                route = horizontal
                    ? new List<Point> { originalPath[0], new(originalPath[0].X, coordinate), new(originalPath[1].X, coordinate), originalPath[1] }
                    : new List<Point> { originalPath[0], new(coordinate, originalPath[0].Y), new(coordinate, originalPath[1].Y), originalPath[1] };
            }
            else if (horizontal && i == 0)
            {
                route[1] = new Point(route[1].X, coordinate); route.Insert(1, new Point(route[0].X, coordinate));
            }
            else if (horizontal && i == route.Count - 2)
            {
                route[^2] = new Point(route[^2].X, coordinate); route.Insert(route.Count - 1, new Point(route[^1].X, coordinate));
            }
            else if (horizontal) { route[i] = new Point(route[i].X, coordinate); route[i + 1] = new Point(route[i + 1].X, coordinate); }
            else if (i == 0)
            {
                route[1] = new Point(coordinate, route[1].Y); route.Insert(1, new Point(coordinate, route[0].Y));
            }
            else if (i == route.Count - 2)
            {
                route[^2] = new Point(coordinate, route[^2].Y); route.Insert(route.Count - 1, new Point(coordinate, route[^1].Y));
            }
            else { route[i] = new Point(coordinate, route[i].Y); route[i + 1] = new Point(coordinate, route[i + 1].Y); }
            for (var j = route.Count - 2; j > 0; j--)
                if ((Math.Abs(route[j - 1].X - route[j].X) < 0.5 && Math.Abs(route[j].X - route[j + 1].X) < 0.5) ||
                    (Math.Abs(route[j - 1].Y - route[j].Y) < 0.5 && Math.Abs(route[j].Y - route[j + 1].Y) < 0.5)) route.RemoveAt(j);
            var wire = _project.Wires.FirstOrDefault(w => w.Id == movingWire);
            if (wire is not null) wire.Waypoints = route.Skip(1).Take(Math.Max(0, route.Count - 2)).Select(p => new WireWaypoint { X = p.X, Y = p.Y }).ToList();
            Redraw();
            return;
        }

        if (!_simulator.IsRunning && IsLeft(e) && _wireStart is null && _terminalBatchSource.Count == 1 &&
            _terminalPressPosition is Point press && Len(_cursorPosition - press) > Math.Max(8, 8 / _zoom))
        {
            var source = _terminalBatchSource[0];
            _wireStart = new TerminalRef(source.ComponentId, source.TerminalId);
            _wireMode = true;
            _terminalBatchSource.Clear();
            _terminalBatchSourceComponent = null;
            _terminalPressPosition = null;
        }

        if (_selectingArea && !_simulator.IsRunning && IsLeft(e))
        {
            _selectionCurrent = _cursorPosition;
            Redraw();
            return;
        }

        if (_dragging && !_simulator.IsRunning && IsLeft(e))
        {
            var delta = _cursorPosition - _dragStart;
            if (!_dragUndoSaved && Len(delta) > 1)
            {
                PushUndoState();
                _dragUndoSaved = true;
            }

            foreach (var origin in _dragOrigins)
            {
                var component = _project.Components.FirstOrDefault(c => c.Id == origin.Key);
                if (component is null) continue;
                component.X = Snap(origin.Value.X + delta.X);
                component.Y = Snap(origin.Value.Y + delta.Y);
            }

            foreach (var routeOrigin in _wireDragOrigins)
            {
                var wire = _project.Wires.FirstOrDefault(w => w.Id == routeOrigin.Key);
                if (wire is null) continue;
                var startMoves = _dragOrigins.ContainsKey(wire.StartComponentId);
                var endMoves = _dragOrigins.ContainsKey(wire.EndComponentId);
                wire.Waypoints = routeOrigin.Value.Select((point, index) =>
                {
                    var followsMovedEnd = (startMoves && (endMoves || index == 0)) ||
                                          (endMoves && !startMoves && index == routeOrigin.Value.Count - 1);
                    return new WireWaypoint
                    {
                        X = followsMovedEnd ? Snap(point.X + delta.X) : point.X,
                        Y = followsMovedEnd ? Snap(point.Y + delta.Y) : point.Y
                    };
                }).ToList();
            }
            Redraw();
            return;
        }

        var prevHover = _hoverTerminal;
        _hoverTerminal = !_simulator.IsRunning
            ? FindNearestTerminal(_cursorPosition, Math.Clamp(8 / _zoom, 3.5, 12))
            : null;

        // Перерисовываем только если реально что-то изменилось:
        // идёт перетаскивание, режим вставки/провода или изменилась подсвеченная клемма
        if (_dragging || _selectingArea || _movingWireId is not null || 
            _placingKind is not null || _wireMode || prevHover != _hoverTerminal)
        {
            Redraw();
        }
    }

    private void Canvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pointer = e.Pointer;
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (_movingWireId is not null)
        {
            _movingWireId = null; _wireMoveOriginalPath = null; _wireMoveUndoSaved = false;
            ReleaseCapture(); Redraw();
            return;
        }
        if (_wireMode && _wireStart is not null)
        {
            var target = FindNearestTerminal(GetWorldPosition(e), Math.Clamp(8 / _zoom, 3.5, 12));
            if (target is not null && !(target.Value.ComponentId == _wireStart.Value.ComponentId && target.Value.TerminalId == _wireStart.Value.TerminalId))
            {
                var a = _project.Components.FirstOrDefault(c => c.Id == _wireStart.Value.ComponentId);
                var b = _project.Components.FirstOrDefault(c => c.Id == target.Value.ComponentId);
                if (a is not null && b is not null)
                {
                    PushUndoState();
                    var path = BuildWirePathAvoidingElements(TerminalPosition(a, _wireStart.Value.TerminalId), TerminalPosition(b, target.Value.TerminalId), a.Id, b.Id);
                    _project.Wires.Add(new WireModel
                    {
                        StartComponentId = a.Id, StartTerminalId = _wireStart.Value.TerminalId,
                        EndComponentId = b.Id, EndTerminalId = target.Value.TerminalId,
                        ColorKey = _selectedWireColorKey,
                        Waypoints = path.Skip(1).Take(Math.Max(0, path.Count - 2)).Select(p => new WireWaypoint { X = p.X, Y = p.Y }).ToList()
                    });
                    _wireStart = null; _wireMode = false; _wireWaypoints.Clear(); _hoverTerminal = null;
                    StatusText.Text = "Провод проложен.";
                    Redraw();
                    return;
                }
            }
            _terminalPressPosition = null;
        }
        if (_selectingArea)
        {
            _selectionCurrent = GetWorldPosition(e);
            _selectingArea = false;
            ReleaseCapture();

            var selectionRect = new Rect(
                new Point(Math.Min(_selectionStart.X, _selectionCurrent.X), Math.Min(_selectionStart.Y, _selectionCurrent.Y)),
                new Point(Math.Max(_selectionStart.X, _selectionCurrent.X), Math.Max(_selectionStart.Y, _selectionCurrent.Y)));
            if (selectionRect.Width >= 4 || selectionRect.Height >= 4)
            {
                foreach (var component in _project.Components)
                {
                    if (selectionRect.Contains(new Point(component.X, component.Y)))
                        _selectedComponentIds.Add(component.Id);
                }
            }

            _selected = _project.Components.LastOrDefault(c => _selectedComponentIds.Contains(c.Id));
            _dragOrigins.Clear();
            if (_selectedComponentIds.Count == 0)
                StatusText.Text = "Выделение снято.";
            else
                StatusText.Text = $"Выделено элементов: {_selectedComponentIds.Count}. Перетащи любой из них, чтобы переместить всю группу.";
            Redraw();
        }

        _dragging = false;
        _dragUndoSaved = false;
        _dragOrigins.Clear();
        _wireDragOrigins.Clear();
        if (!_panning)
            ReleaseCapture();

        if (_pressedButtonId is Guid id)
        {
            _simulator.SetButtonsByTag(id, false, _project);
            _pressedButtonId = null;
            ReleaseCapture();
            Redraw();
            StatusText.Text = _simulator.Message;
        }
    }

    private void Canvas_PointerExited(object? sender, PointerEventArgs e)
    {
        _hoverTerminal = null;
        if (_placingKind is null && _wireStart is null) Redraw();
    }

    private void HandleTerminalBatchClick((Guid ComponentId, string TerminalId) terminal)
    {
        if (_terminalBatchSourceComponent is null)
        {
            _terminalBatchSourceComponent = terminal.ComponentId;
            _terminalBatchSource.Add(terminal);
            StatusText.Text = "Начальная клемма выбрана. Можно выбрать ещё клеммы этого устройства, затем клеммы второго устройства в том же порядке.";
            return;
        }

        if (_terminalBatchTargetComponent is null && terminal.ComponentId == _terminalBatchSourceComponent)
        {
            if (!_terminalBatchSource.Contains(terminal)) _terminalBatchSource.Add(terminal);
            StatusText.Text = $"Выбрано клемм первого устройства: {_terminalBatchSource.Count}. Теперь выбери столько же клемм второго устройства в нужном порядке.";
            return;
        }

        if (_terminalBatchTargetComponent is null) _terminalBatchTargetComponent = terminal.ComponentId;
        if (terminal.ComponentId != _terminalBatchTargetComponent || _terminalBatchTarget.Contains(terminal)) return;
        _terminalBatchTarget.Add(terminal);

        if (_terminalBatchSource.Count == 1 || _terminalBatchTarget.Count >= _terminalBatchSource.Count)
        {
            PushUndoState();
            var pairs = MatchTerminalGroups(_terminalBatchSource, _terminalBatchTarget);
            foreach (var (source, target) in pairs)
            {
                var a = _project.Components.FirstOrDefault(c => c.Id == source.ComponentId);
                var b = _project.Components.FirstOrDefault(c => c.Id == target.ComponentId);
                if (a is null || b is null) continue;
                var path = BuildWirePathAvoidingElements(TerminalPosition(a, source.TerminalId), TerminalPosition(b, target.TerminalId), a.Id, b.Id);
                _project.Wires.Add(new WireModel
                {
                    StartComponentId = a.Id, StartTerminalId = source.TerminalId,
                    EndComponentId = b.Id, EndTerminalId = target.TerminalId,
                    ColorKey = _selectedWireColorKey,
                    Waypoints = path.Skip(1).Take(Math.Max(0, path.Count - 2)).Select(p => new WireWaypoint { X = p.X, Y = p.Y }).ToList()
                });
            }
            StatusText.Text = $"Проложено проводов: {Math.Min(_terminalBatchSource.Count, _terminalBatchTarget.Count)}.";
            _terminalBatchSource.Clear();
            _terminalBatchTarget.Clear();
            _terminalBatchSourceComponent = null;
            _terminalBatchTargetComponent = null;
            return;
        }
        StatusText.Text = $"Выбрано клемм второго устройства: {_terminalBatchTarget.Count} из {_terminalBatchSource.Count}.";
    }

    private List<((Guid ComponentId, string TerminalId) Source, (Guid ComponentId, string TerminalId) Target)> MatchTerminalGroups(
        IReadOnlyList<(Guid ComponentId, string TerminalId)> sourceInput,
        IReadOnlyList<(Guid ComponentId, string TerminalId)> targetInput)
    {
        var count = Math.Min(sourceInput.Count, targetInput.Count);
        var source = sourceInput.Select(t => (Terminal: t, Position: TerminalPosition(_project.Components.First(c => c.Id == t.ComponentId), t.TerminalId)))
            .OrderBy(x => x.Position.Y).ThenBy(x => x.Position.X).ToArray();
        var targets = targetInput.Select(t => (Terminal: t, Position: TerminalPosition(_project.Components.First(c => c.Id == t.ComponentId), t.TerminalId)))
            .OrderBy(x => x.Position.Y).ThenBy(x => x.Position.X).ToArray();
        var used = new bool[targets.Length];
        var current = new int[count];
        var best = new int[count];
        var bestCost = double.PositiveInfinity;
        void Search(int index, double cost)
        {
            if (index == count)
            {
                if (cost < bestCost) { bestCost = cost; Array.Copy(current, best, count); }
                return;
            }
            for (var j = 0; j < targets.Length; j++)
            {
                if (used[j]) continue;
                var nextCost = cost + Math.Abs(source[index].Position.X - targets[j].Position.X) + Math.Abs(source[index].Position.Y - targets[j].Position.Y);
                if (nextCost >= bestCost) continue;
                used[j] = true; current[index] = j;
                Search(index + 1, nextCost);
                used[j] = false;
            }
        }
        Search(0, 0);
        return Enumerable.Range(0, count).Select(i => (source[i].Terminal, targets[best[i]].Terminal)).ToList();
    }

    private void Canvas_RightDown(PointerPressedEventArgs e)
    {
        var point = GetWorldPosition(e);
        var component = HitComponent(point);
        if (component?.Kind is ComponentKind.Lamp or ComponentKind.BlinkingLamp)
        {
            BuildLampGlowColorMenu(component, point).Open(SchematicCanvas);
            e.Handled = true;
            return;
        }
        if (_simulator.IsRunning) return;

        var wireId = HitWire(point);
        if (wireId is Guid id)
        {
            var wire = _project.Wires.FirstOrDefault(w => w.Id == id);
            if (wire is not null)
            {
                _selectedWireId = id;
                _selected = null;
                _selectedComponentIds.Clear();
                Redraw();
                BuildExistingWireColorMenu(wire, point).Open(SchematicCanvas);
                e.Handled = true;
                return;
            }
        }

        CancelTool();
        e.Handled = true;
    }

    private ContextMenu BuildExistingWireColorMenu(WireModel wire, Point point)
    {
        var menu = new ContextMenu
        {
            Placement = PlacementMode.Pointer
        };

        var heading = new MenuItem { Header = "Цвет провода", IsEnabled = false };
        menu.Items.Add(heading);
        menu.Items.Add(new Separator());

        foreach (var color in WireColors)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border
            {
                Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0),
                Background = Brush(color.Hex),
                BorderBrush = Brush("#CBD5E1"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3)
            });
            row.Children.Add(new TextBlock { Text = color.Label, VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem
            {
                Header = row,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = wire.ColorKey == color.Key
            };
            item.Click += (_, _) =>
            {
                if (_simulator.IsRunning) return;
                if (wire.ColorKey == color.Key) return;
                PushUndoState();
                wire.ColorKey = color.Key;
                _selectedWireColorKey = color.Key;
                _selectedWireId = null;
                _selected = null;
                _selectedComponentIds.Clear();
                StatusText.Text = $"Цвет выбранного провода: {color.Label}.";
                menu.Close();
                Dispatcher.UIThread.Post(() =>
                {
                    _selectedWireId = null;
                    Redraw();
                    SchematicCanvas.InvalidateVisual();
                }, DispatcherPriority.Render);
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    private ContextMenu BuildLampGlowColorMenu(ComponentModel lamp, Point point)
    {
        var menu = new ContextMenu
        {
            Placement = PlacementMode.Pointer
        };
        menu.Items.Add(new MenuItem { Header = "Цвет свечения", IsEnabled = false });
        menu.Items.Add(new Separator());
        foreach (var color in LampGlowColors)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border
            {
                Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0),
                Background = Brush(color.Hex), BorderBrush = Brush("#CBD5E1"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8)
            });
            row.Children.Add(new TextBlock { Text = color.Label, VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Header = row, ToggleType = MenuItemToggleType.CheckBox, IsChecked = lamp.GlowColorHex == color.Hex };
            item.Click += (_, _) =>
            {
                if (lamp.GlowColorHex == color.Hex) return;
                if (!_simulator.IsRunning) PushUndoState();
                lamp.GlowColorHex = color.Hex;
                menu.Close();
                Redraw();
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    private void Canvas_KeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Key is Key.Delete or Key.Back) && !_simulator.IsRunning) { DeleteSelected(); e.Handled = true; }
        if (e.Key == Key.Escape) { CancelTool(); e.Handled = true; }
    }

    private void Window_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox or ComboBox) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.S && ctrl)
        {
            _ = SaveProjectAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Z && ctrl)
        {
            if (!_simulator.IsRunning) UndoLastAction();
            else StatusText.Text = "Останови симуляцию перед отменой действия.";
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Back or Key.Delete)
        {
            if (!_simulator.IsRunning) DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.R)
        {
            if (!_simulator.IsRunning) RotateSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelTool();
            e.Handled = true;
        }
    }

    private ProjectDocument CloneProject(ProjectDocument source)
    {
        return new ProjectDocument
        {
            SchemaVersion = source.SchemaVersion,
            Name = source.Name,
            Components = source.Components.Select(c => new ComponentModel
            {
                Id = c.Id,
                Kind = c.Kind,
                Tag = c.Tag,
                X = c.X,
                Y = c.Y,
                Phases = c.Phases,
                Voltage = c.Voltage,
                Rotation = c.Rotation,
                GlowColorHex = c.GlowColorHex
            }).ToList(),
            Wires = source.Wires.Select(w => new WireModel
            {
                Id = w.Id,
                StartComponentId = w.StartComponentId,
                StartTerminalId = w.StartTerminalId,
                EndComponentId = w.EndComponentId,
                EndTerminalId = w.EndTerminalId,
                ColorKey = w.ColorKey,
                Waypoints = (w.Waypoints ?? new List<WireWaypoint>())
                    .Select(p => new WireWaypoint { X = p.X, Y = p.Y }).ToList()
            }).ToList()
        };
    }

    private void PushUndoState()
    {
        if (_simulator.IsRunning) return;
        _undoStack.Push(CloneProject(_project));
        if (_undoStack.Count > MaxUndoSteps)
        {
            // Keep the most recent operations without letting the history grow without bound.
            var keep = _undoStack.Take(MaxUndoSteps).Reverse().ToList();
            _undoStack.Clear();
            foreach (var state in keep) _undoStack.Push(state);
        }
    }

    private void UndoLastAction()
    {
        if (_undoStack.Count == 0)
        {
            StatusText.Text = "Отменять больше нечего.";
            return;
        }

        ReleaseCapture();
        _project = _undoStack.Pop();
        _selected = null;
        _selectedComponentIds.Clear();
        _selectedWireId = null;
        _wireStart = null;
        _wireWaypoints.Clear();
        _wireMode = false;
        _selectingArea = false;
        _placingKind = null;
        _dragging = false;
        _dragUndoSaved = false;
        _dragOrigins.Clear();
        _wireDragOrigins.Clear();
        _hoverTerminal = null;
        Redraw();
        StatusText.Text = "Последнее действие отменено (Ctrl+Z).";
    }

    private void CancelTool()
    {
        _placingKind = null; _wireMode = false; _wireStart = null;
        _wireWaypoints.Clear();
        _terminalBatchSource.Clear(); _terminalBatchTarget.Clear();
        _terminalBatchSourceComponent = null; _terminalBatchTargetComponent = null;
        _terminalPressPosition = null; _selected = null; _selectedComponentIds.Clear(); _selectedWireId = null;
        _movingWireId = null; _wireMoveOriginalPath = null; _wireMoveUndoSaved = false;
        _hoverTerminal = null;
        _selectingArea = false;
        ReleaseCapture();
        Redraw(); StatusText.Text = "Режим инструмента отменён.";
    }

    private ComponentModel? HitComponent(Point point) => _project.Components.AsEnumerable().Reverse()
        .FirstOrDefault(c =>
        {
            var poles = c.Kind switch
            {
                ComponentKind.Breaker1PN or ComponentKind.Breaker2P or ComponentKind.Rcd1PN => 2,
                ComponentKind.Breaker3P or ComponentKind.Contactor3P => 3,
                ComponentKind.Contactor4P => 4,
                ComponentKind.Contactor2P => 2,
                ComponentKind.Breaker3PN or ComponentKind.Rcd3PN or ComponentKind.ThermalRelay or ComponentKind.SurgeProtector3PN => 4,
                _ => 1
            };
            var extent = poles == 1 ? 58 : 25.0 * (poles - 1) + 58;
            return new Rect(c.X - extent, c.Y - 46, extent * 2, 92).Contains(point);
        });

    private TerminalRef? FindNearestTerminal(Point point, double radius)
    {
        TerminalRef? best = null;
        var bestDistance = radius;
        foreach (var c in _project.Components)
        {
            foreach (var terminal in ComponentTerminals.Get(c))
            {
                var pos = TerminalPosition(c, terminal.Id);
                var distance = Len(pos - point);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = new TerminalRef(c.Id, terminal.Id);
                }
            }
        }
        return best;
    }

    private Point TerminalPosition(ComponentModel c, string terminalId)
    {
        var term = ComponentTerminals.Get(c).FirstOrDefault(t => t.Id == terminalId)
                   ?? ComponentTerminals.Get(c).FirstOrDefault();
        if (term is null) return new Point(c.X, c.Y);

        var angle = ((c.Rotation % 360) + 360) % 360 * Math.PI / 180.0;
        var x = term.OffsetX * Math.Cos(angle) - term.OffsetY * Math.Sin(angle);
        var y = term.OffsetX * Math.Sin(angle) + term.OffsetY * Math.Cos(angle);
        return new Point(c.X + x, c.Y + y);
    }

    private static bool UsesSequentialContactNumbers(ComponentModel c) =>
        c.Kind is ComponentKind.PushButtonNO or ComponentKind.PushButtonNC
            or ComponentKind.ToggleSwitchNO or ComponentKind.ToggleSwitchNC
            or ComponentKind.AuxContactNO or ComponentKind.AuxContactNC or ComponentKind.ConContact;

    private (string, string) GetContactTerminalNumbers(ComponentModel component)
    {
        var numbered = _project.Components.Where(UsesSequentialContactNumbers).ToList();
        var index = numbered.FindIndex(c => c.Id == component.Id);
        // A preview component is not in the project yet; show the numbers it will receive next.
        if (index < 0) index = numbered.Count;
        return ((index * 2 + 1).ToString(), (index * 2 + 2).ToString());
    }

    private string TerminalLabel(ComponentModel c, string terminalId)
    {
        if (UsesSequentialContactNumbers(c))
        {
            var numbers = GetContactTerminalNumbers(c);
            return terminalId == "L" ? numbers.Item1 : numbers.Item2;
        }
        return ComponentTerminals.Get(c).First(t => t.Id == terminalId).Label;
    }

    private void DeleteSelected()
    {
        if (_simulator.IsRunning) return;
        if (_selectedWireId is Guid wireId)
        {
            if (!_project.Wires.Any(w => w.Id == wireId)) return;
            PushUndoState();
            _project.Wires.RemoveAll(w => w.Id == wireId);
            PruneOrphanWireJunctions();
            _selectedWireId = null;
            Redraw(); StatusText.Text = "Выбранный провод удалён.";
            return;
        }
        if (_selectedComponentIds.Count == 0 && _selected is not null)
            _selectedComponentIds.Add(_selected.Id);
        if (_selectedComponentIds.Count == 0) return;

        var ids = _selectedComponentIds.ToHashSet();
        PushUndoState();
        _project.Components.RemoveAll(c => ids.Contains(c.Id));
        _project.Wires.RemoveAll(w => ids.Contains(w.StartComponentId) || ids.Contains(w.EndComponentId));
        PruneOrphanWireJunctions();
        _selected = null;
        _selectedComponentIds.Clear();
        Redraw();
        StatusText.Text = $"Удалено элементов: {ids.Count}, вместе с подключёнными проводами.";
    }

    private void PruneOrphanWireJunctions()
    {
        _project.Components.RemoveAll(c => c.Kind == ComponentKind.WireJunction &&
            !_project.Wires.Any(w => w.StartComponentId == c.Id || w.EndComponentId == c.Id));
    }

    // private void RotateSelected_Click(object sender, RoutedEventArgs e) => RotateSelected();
    private void RotateSelected()
    {
        if (_simulator.IsRunning) { StatusText.Text = "Останови симуляцию, чтобы поворачивать элементы."; return; }
        if (_placingKind is not null && _selected is null)
        {
            _placingRotation = (_placingRotation + 90) % 360;
            Redraw();
            StatusText.Text = $"Поворот предпросмотра: {_placingRotation}°. Щёлкни по полю, чтобы установить элемент.";
            return;
        }
        if (_selectedComponentIds.Count == 0 && _selected is not null)
            _selectedComponentIds.Add(_selected.Id);
        if (_selectedComponentIds.Count == 0) { StatusText.Text = "Сначала выбери элемент на рабочем поле."; return; }

        PushUndoState();
        foreach (var component in _project.Components.Where(c => _selectedComponentIds.Contains(c.Id)))
            component.Rotation = (component.Rotation + 90) % 360;
        Redraw();
        StatusText.Text = $"Повернуто элементов: {_selectedComponentIds.Count}. Подключения сохранены.";
    }

private void Redraw()
    {
        try
        {
            _currentFlowDots.Clear();
            _blinkingLampVisuals.Clear();

            // The grid never changes, so it is built once and kept as the first children of the canvas.
            if (_gridCount < 0)
            {
                SchematicCanvas.Children.Clear();
                DrawGrid();
                _gridCount = SchematicCanvas.Children.Count;
            }
            else if (SchematicCanvas.Children.Count > _gridCount)
            {
                SchematicCanvas.Children.RemoveRange(_gridCount, SchematicCanvas.Children.Count - _gridCount);
            }

            foreach (var wire in _project.Wires)
            {
                var start = _project.Components.FirstOrDefault(c => c.Id == wire.StartComponentId);
                var end = _project.Components.FirstOrDefault(c => c.Id == wire.EndComponentId);
                if (start is null || end is null) continue;

                var a = TerminalPosition(start, wire.StartTerminalId);
                var b = TerminalPosition(end, wire.EndTerminalId);
                var path = BuildWirePath(a, b, wire.Waypoints?.Select(p => new Point(p.X, p.Y)));
                DrawWirePath(path, WireColorHex(wire.ColorKey), selected: _selectedWireId == wire.Id);
                if (_simulator.IsRunning && IsCurrentFlowWire(wire))
                    DrawCurrentFlowDots(path, _simulator.IsCurrentFlowForward(wire.Id));
            }

            foreach (var c in _project.Components)
            {
                var color = !_simulator.IsRunning && _selectedComponentIds.Contains(c.Id)
                    ? "#2563EB"
                    : GetComponentColor(c);
                DrawComponent(c, Brush(color));
            }

            if (_simulator.IsRunning)
                foreach (var shortPoint in _simulator.ShortCircuitPoints)
                    DrawShortCircuitSpark(new Point(shortPoint.X, shortPoint.Y));

            if (_placingKind is not null && !_simulator.IsRunning)
            {
                var preview = new ComponentModel
                {
                    Kind = _placingKind.Value, Tag = "?", X = Snap(_cursorPosition.X), Y = Snap(_cursorPosition.Y),
                    Rotation = _placingRotation,
                    Phases = _placingKind == ComponentKind.AcSupply ? _pendingAcPhases : 1,
                    Voltage = _placingKind == ComponentKind.AcSupply ? _pendingAcVoltage : 230
                };
                DrawComponent(preview, Brush("#7896C4"), true);
            }

            if (_wireStart is not null && _wireMode)
            {
                var startComponent = _project.Components.FirstOrDefault(c => c.Id == _wireStart.Value.ComponentId);
                if (startComponent is not null)
                {
                    var a = TerminalPosition(startComponent, _wireStart.Value.TerminalId);
                    var hoverComp = _hoverTerminal is not null
                        ? _project.Components.FirstOrDefault(c => c.Id == _hoverTerminal.Value.ComponentId)
                        : null;

                    var target = hoverComp is not null
                        ? TerminalPosition(hoverComp, _hoverTerminal!.Value.TerminalId)
                        : new Point(Snap(_cursorPosition.X), Snap(_cursorPosition.Y));

                    var path = BuildWirePath(a, target, _wireWaypoints);
                    DrawWirePath(path, WireColorHex(_selectedWireColorKey), dashed: true);
                    DrawTerminal(a.X, a.Y, true);
                }
            }

            if (_hoverTerminal is not null)
            {
                var hoverComp = _project.Components.FirstOrDefault(c => c.Id == _hoverTerminal.Value.ComponentId);
                if (hoverComp is not null)
                {
                    var p = TerminalPosition(hoverComp, _hoverTerminal.Value.TerminalId);
                    DrawTerminal(p.X, p.Y, true);
                }
            }

            if (_selectingArea)
            {
                var rect = new Rect(new Point(Math.Min(_selectionStart.X, _selectionCurrent.X), Math.Min(_selectionStart.Y, _selectionCurrent.Y)),
                    new Point(Math.Max(_selectionStart.X, _selectionCurrent.X), Math.Max(_selectionStart.Y, _selectionCurrent.Y)));
                var selectionShape = new Rectangle
                {
                    Width = rect.Width,
                    Height = rect.Height,
                    Stroke = Brush("#2563EB"),
                    StrokeThickness = 1.2,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                    Fill = Brush("#DBEAFE"),
                    Opacity = 0.35,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(selectionShape, rect.Left);
                Canvas.SetTop(selectionShape, rect.Top);
                SchematicCanvas.Children.Add(selectionShape);
            }
            ApplyZoomToSchematicText(SchematicCanvas);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка отрисовки: {ex.Message}";
        }
    }

    private string GetComponentColor(ComponentModel c)
    {
        if (!_simulator.IsRunning) return _darkTheme ? "#FFFFFF" : "#000000";
        if (c.Kind is ComponentKind.Coil or ComponentKind.Lamp or ComponentKind.BlinkingLamp or ComponentKind.Motor)
            return _simulator.IsEnergized(c.Id) ? "#15803D" : "#64748B";
        if (c.Kind is ComponentKind.AcSupply or ComponentKind.Supply24V)
            return _simulator.GetState(c.Id).StartsWith("Источник включён", StringComparison.Ordinal) ? "#15803D" : "#64748B";
        if (_simulator.IsEnergized(c.Id)) return "#15803D";
        return "#334155";
    }

    private void DrawGrid()
    {
        var width = Math.Max(1500, double.IsNaN(SchematicCanvas.Width) ? 0 : SchematicCanvas.Width);
        var height = Math.Max(1000, double.IsNaN(SchematicCanvas.Height) ? 0 : SchematicCanvas.Height);
        var brush = Brush(_darkTheme ? "#303030" : "#E8EDF3");
        SchematicCanvas.Children.Add(new GridLayer(width, height, brush, 0.65, GridSize));
        var frame = new Rectangle { Width = width, Height = height, Stroke = Brush(_darkTheme ? "#8A8A8A" : "#334155"), StrokeThickness = 2, Fill = Brushes.Transparent, IsHitTestVisible = false };
        Canvas.SetLeft(frame, 0); Canvas.SetTop(frame, 0); SchematicCanvas.Children.Add(frame);
    }

    private static void AppendDistinctPoint(List<Point> points, Point point)
    {
        if (points.Count == 0 || Len(points[^1] - point) > 0.5)
            points.Add(point);
    }

    private static Point? GetOrthogonalBend(Point from, Point to)
    {
        var dx = Math.Abs(to.X - from.X);
        var dy = Math.Abs(to.Y - from.Y);
        if (dx < 0.5 || dy < 0.5) return null;

        // The elbow follows the dominant direction of the cursor movement.
        return dx >= dy
            ? new Point(to.X, from.Y)
            : new Point(from.X, to.Y);
    }

    private List<Point> BuildWirePath(Point start, Point end, IEnumerable<Point>? waypoints)
    {
        var points = new List<Point> { start };
        if (waypoints is not null)
        {
            foreach (var waypoint in waypoints)
            {
                var waypointBend = GetOrthogonalBend(points[^1], waypoint);
                if (waypointBend is Point waypointElbow) AppendDistinctPoint(points, waypointElbow);
                AppendDistinctPoint(points, waypoint);
            }
        }

        var finalBend = GetOrthogonalBend(points[^1], end);
        if (finalBend is Point finalElbow) AppendDistinctPoint(points, finalElbow);
        AppendDistinctPoint(points, end);

        // Collapse every collinear run, including a reversal, so changing the
        // route direction cannot leave a short retraced tail behind.
        for (var i = points.Count - 2; i > 0; i--)
        {
            var a = points[i - 1];
            var b = points[i];
            var c = points[i + 1];
            var sameX = Math.Abs(a.X - b.X) < 0.5 && Math.Abs(b.X - c.X) < 0.5;
            var sameY = Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(b.Y - c.Y) < 0.5;
            if (sameX || sameY)
                points.RemoveAt(i);
        }
        return points;
    }

    private List<Point> BuildWirePathAvoidingElements(Point start, Point end, Guid startId, Guid endId)
    {
        var obstacles = _project.Components.Where(c => c.Id != startId && c.Id != endId)
            .Select(c => new Rect(c.X - 58, c.Y - 42, 116, 84)).ToList();
        foreach (var wire in _project.Wires)
        {
            var a = _project.Components.FirstOrDefault(c => c.Id == wire.StartComponentId);
            var b = _project.Components.FirstOrDefault(c => c.Id == wire.EndComponentId);
            if (a is null || b is null) continue;
            var route = new List<Point> { TerminalPosition(a, wire.StartTerminalId) };
            route.AddRange(wire.Waypoints?.Select(p => new Point(p.X, p.Y)) ?? Enumerable.Empty<Point>());
            route.Add(TerminalPosition(b, wire.EndTerminalId));
            for (var i = 1; i < route.Count; i++)
            {
                var p1 = route[i - 1]; var p2 = route[i];
                obstacles.Add(new Rect(Math.Min(p1.X, p2.X) - 7, Math.Min(p1.Y, p2.Y) - 7,
                    Math.Abs(p1.X - p2.X) + 14, Math.Abs(p1.Y - p2.Y) + 14));
            }
        }
        var xs = new List<double> { start.X, end.X };
        var ys = new List<double> { start.Y, end.Y };
        foreach (var r in obstacles)
        {
            xs.Add(r.Left); xs.Add(r.Right);
            ys.Add(r.Top); ys.Add(r.Bottom);
        }
        xs = xs.Distinct().OrderBy(x => x).ToList();
        ys = ys.Distinct().OrderBy(y => y).ToList();
        var width = xs.Count;
        var count = width * ys.Count;
        var startIndex = xs.FindIndex(x => Math.Abs(x - start.X) < 0.01) + ys.FindIndex(y => Math.Abs(y - start.Y) < 0.01) * width;
        var endIndex = xs.FindIndex(x => Math.Abs(x - end.X) < 0.01) + ys.FindIndex(y => Math.Abs(y - end.Y) < 0.01) * width;
        var distance = Enumerable.Repeat(double.PositiveInfinity, count).ToArray();
        var previous = Enumerable.Repeat(-1, count).ToArray();
        var queue = new PriorityQueue<int, double>();
        distance[startIndex] = 0;
        queue.Enqueue(startIndex, 0);
        while (queue.TryDequeue(out var current, out var currentCost))
        {
            if (currentCost > distance[current] + 0.001) continue;
            if (current == endIndex) break;
            var neighbors = new[] { current - 1, current + 1, current - width, current + width }
                .Where(n => n >= 0 && n < count && (n / width == current / width || n % width == current % width));
            foreach (var next in neighbors)
            {
                var a = new Point(xs[current % width], ys[current / width]);
                var b = new Point(xs[next % width], ys[next / width]);
                if (obstacles.Any(r => SegmentCrossesRect(a, b, r))) continue;
                var nextCost = currentCost + Len(b - a) + 0.01;
                if (nextCost >= distance[next]) continue;
                distance[next] = nextCost; previous[next] = current; queue.Enqueue(next, nextCost);
            }
        }
        if (double.IsPositiveInfinity(distance[endIndex])) return BuildWirePath(start, end, null);
        var path = new List<Point>();
        for (var node = endIndex; node >= 0; node = previous[node])
        {
            path.Add(node == startIndex ? start : node == endIndex ? end : new Point(xs[node % width], ys[node / width]));
            if (node == startIndex) break;
        }
        path.Reverse();
        for (var i = path.Count - 2; i > 0; i--)
        {
            var a = path[i - 1]; var b = path[i]; var c = path[i + 1];
            if ((Math.Abs(a.X - b.X) < 0.01 && Math.Abs(b.X - c.X) < 0.01) ||
                (Math.Abs(a.Y - b.Y) < 0.01 && Math.Abs(b.Y - c.Y) < 0.01)) path.RemoveAt(i);
        }
        return path;
    }

    private static bool SegmentCrossesRect(Point a, Point b, Rect r)
    {
        if (Math.Abs(a.X - b.X) < 0.5)
            return a.X > r.Left && a.X < r.Right && Math.Max(Math.Min(a.Y, b.Y), r.Top) < Math.Min(Math.Max(a.Y, b.Y), r.Bottom);
        if (Math.Abs(a.Y - b.Y) < 0.5)
            return a.Y > r.Top && a.Y < r.Bottom && Math.Max(Math.Min(a.X, b.X), r.Left) < Math.Min(Math.Max(a.X, b.X), r.Right);
        return true;
    }

    private void DrawWirePath(IReadOnlyList<Point> points, string colorHex, bool dashed = false, bool selected = false)
    {
        if (points.Count < 2) return;
        var dash = dashed ? new DoubleCollection { 4, 3 } : null;

        for (var i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            AddWireSegment(a.X, a.Y, b.X, b.Y,
                selected ? "#F59E0B" : colorHex,
                dash,
                selected ? 5.0 : (dashed ? 2.0 : 2.3),
                dashed ? 0.9 : 1);
        }

        DrawTerminal(points[0].X, points[0].Y);
        DrawTerminal(points[^1].X, points[^1].Y);
    }

    private void DrawCurrentFlowDots(IReadOnlyList<Point> points, bool forward)
    {
        const double spacing = 24;
        const double inset = 6;
        var segments = new List<(Point Start, Point End, double Length)>();
        var totalLength = 0.0;
        for (var i = 0; i < points.Count - 1; i++)
        {
            var delta = points[i + 1] - points[i];
            var length = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            if (length < 0.01) continue;
            segments.Add((points[i], points[i + 1], length));
            totalLength += length;
        }
        if (totalLength <= inset * 2) return;

        for (var distance = inset; distance < totalLength - inset; distance += spacing)
        {
            var dot = new Ellipse
            {
                Width = 4.5, Height = 4.5, Fill = Brush("#FACC15"),
                Stroke = Brush("#854D0E"), StrokeThickness = 0.8,
                IsHitTestVisible = false
            };
            SchematicCanvas.Children.Add(dot);
            _currentFlowDots.Add(new CurrentFlowDotVisual(dot, segments, totalLength, distance, forward));
        }

        UpdateCurrentFlowDots();
    }

    private void UpdateBlinkingLampVisuals()
    {
        foreach (var lamp in _blinkingLampVisuals)
        {
            var lampOn = _blinkOn && _simulator.IsEnergized(lamp.ComponentId);
            var stroke = lampOn ? lamp.OnStroke : lamp.OffStroke;
            lamp.Halo.IsVisible = lampOn;
            lamp.Bulb.Stroke = stroke;
            lamp.Bulb.Fill = lampOn ? lamp.OnStroke : Brushes.Transparent;
            foreach (var line in lamp.Lines)
                line.Stroke = stroke;
            lamp.SignalLine.Stroke = stroke;
        }
    }

    private void UpdateCurrentFlowDots()
    {
        const double spacing = 24;
        const double inset = 6;
        var phase = _currentDotOffset % spacing;
        foreach (var flowDot in _currentFlowDots)
        {
            var distance = flowDot.BaseDistance + phase;
            if (distance >= flowDot.TotalLength - inset)
            {
                flowDot.Element.IsVisible = false;
                continue;
            }

            flowDot.Element.IsVisible = true;
            var pathDistance = flowDot.Forward ? distance : flowDot.TotalLength - distance;
            var remaining = pathDistance;
            var lastSegmentIndex = flowDot.Segments.Count - 1;
            for (var i = 0; i < flowDot.Segments.Count; i++)
            {
                var segment = flowDot.Segments[i];
                if (remaining <= segment.Length || i == lastSegmentIndex)
                {
                    var t = Math.Clamp(remaining / segment.Length, 0.0, 1.0);
                    Canvas.SetLeft(flowDot.Element, segment.Start.X + (segment.End.X - segment.Start.X) * t - 2.25);
                    Canvas.SetTop(flowDot.Element, segment.Start.Y + (segment.End.Y - segment.Start.Y) * t - 2.25);
                    break;
                }
                remaining -= segment.Length;
            }
        }
    }

    private bool IsCurrentFlowWire(WireModel wire)
    {
        return _simulator.IsWireCarryingCurrent(wire.Id);
    }

    private void DrawShortCircuitSpark(Point center)
    {
        var glow = new Ellipse
        {
            Width = 24, Height = 24, Fill = Brush("#FDE047"), Opacity = 0.35,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(glow, center.X - 12); Canvas.SetTop(glow, center.Y - 12);
        SchematicCanvas.Children.Add(glow);
        var spark = new Polygon
        {
            Points = new PointCollection
            {
                new(center.X, center.Y - 11), new(center.X + 3, center.Y - 4),
                new(center.X + 9, center.Y - 6), new(center.X + 5, center.Y),
                new(center.X + 10, center.Y + 3), new(center.X + 2, center.Y + 4),
                new(center.X, center.Y + 11), new(center.X - 3, center.Y + 4),
                new(center.X - 9, center.Y + 6), new(center.X - 5, center.Y),
                new(center.X - 10, center.Y - 3), new(center.X - 2, center.Y - 4)
            },
            Fill = Brush("#FACC15"), Stroke = Brush("#EA580C"), StrokeThickness = 1.5,
            IsHitTestVisible = false
        };
        SchematicCanvas.Children.Add(spark);
    }

    private void AddWireSegment(double x1, double y1, double x2, double y2, string colorHex, DoubleCollection? dash, double thickness, double opacity)
    {
        SchematicCanvas.Children.Add(new Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = Brush(colorHex), StrokeThickness = thickness, StrokeDashArray = dash, Opacity = opacity, IsHitTestVisible = false });
    }

    private void DrawTerminal(double x, double y, bool highlight = false)
    {
        if (highlight)
        {
            var glow = new Ellipse
            {
                Width = 18, Height = 18, Fill = Brush("#BFDBFE"), Opacity = 0.6,
                Stroke = Brush("#60A5FA"), StrokeThickness = 1.2, IsHitTestVisible = false
            };
            Canvas.SetLeft(glow, x - 9); Canvas.SetTop(glow, y - 9); SchematicCanvas.Children.Add(glow);
            var core = new Ellipse { Width = 7, Height = 7, Fill = Brush("#1D4ED8"), IsHitTestVisible = false };
            Canvas.SetLeft(core, x - 3.5); Canvas.SetTop(core, y - 3.5); SchematicCanvas.Children.Add(core);
        }
        else
        {
            var dot = new Ellipse { Width = 5.5, Height = 5.5, Fill = Brush("#2563EB"), IsHitTestVisible = false };
            Canvas.SetLeft(dot, x - 2.75); Canvas.SetTop(dot, y - 2.75); SchematicCanvas.Children.Add(dot);
        }
    }

    private void DrawComponent(ComponentModel c, IBrush color, bool preview = false)
    {
        var group = new Canvas { Width = 100, Height = 64, Opacity = preview ? 0.55 : 1, IsHitTestVisible = false };
        var stroke = color;
        var contactClosed = c.Kind is ComponentKind.Contactor2P or ComponentKind.Contactor3P or ComponentKind.Contactor4P
            ? !preview && _simulator.IsRunning && _simulator.IsContactorClosed(c.Id, _project)
            : c.Kind == ComponentKind.ThermalRelay
            ? true
            : _simulator.IsRunning
                ? _simulator.GetContactClosed(c.Id, c.Kind)
            : c.Kind is ComponentKind.PushButtonNC or ComponentKind.ToggleSwitchNC or ComponentKind.AuxContactNC or ComponentKind.OverloadNC or ComponentKind.ThermalRelayAuxNC;

        switch (c.Kind)
        {
            case ComponentKind.PowerPhase:
            case ComponentKind.PowerNeutral:
            case ComponentKind.PowerEarth:
            case ComponentKind.PowerPhaseNeutral:
            case ComponentKind.PowerPhaseNeutralEarth:
            case ComponentKind.PowerThreePhase:
            case ComponentKind.PowerThreePhaseEarth:
            case ComponentKind.PowerThreePhaseNeutral:
            case ComponentKind.PowerThreePhaseNeutralEarth:
            case ComponentKind.PowerPlus:
            case ComponentKind.PowerMinus:
            case ComponentKind.PowerDcEarth:
                DrawPowerMarker(group, c.Kind, stroke, c.Rotation);
                break;
            case ComponentKind.Breaker1P: case ComponentKind.Breaker1PN: case ComponentKind.Breaker2P:
            case ComponentKind.Breaker3P: case ComponentKind.Breaker3PN: case ComponentKind.Rcd1PN:
            case ComponentKind.Rcd3PN: case ComponentKind.ThermalRelay: case ComponentKind.SurgeProtector1P:
            case ComponentKind.SurgeProtector3PN:
                DrawMultiPoleDevice(group, c.Kind, stroke, contactClosed, c.Tag);
                break;
            case ComponentKind.Contactor2P:
            case ComponentKind.Contactor3P:
            case ComponentKind.Contactor4P:
                var contactorPoleCount = c.Kind == ComponentKind.Contactor2P ? 2 : c.Kind == ComponentKind.Contactor3P ? 3 : 4;
                var conductingPoles = !preview && _simulator.IsRunning
                    ? Enumerable.Range(0, contactorPoleCount).Where(p => _simulator.IsContactorPoleConducting(c.Id, p)).ToHashSet()
                    : null;
                DrawMultiPoleDevice(group, c.Kind, stroke, contactClosed, c.Tag, conductingPoles);
                break;

            case ComponentKind.LimitSwitchNO:
            case ComponentKind.LimitSwitchNC:
                var isLimitNC = c.Kind == ComponentKind.LimitSwitchNC;
                var limitContactClosed = preview ? isLimitNC : (_simulator.IsRunning ? _simulator.GetContactClosed(c.Id, c.Kind) : isLimitNC);
                var num1 = isLimitNC ? "11" : "13";
                var num2 = isLimitNC ? "12" : "14";

                DrawContactSymbol(group, stroke, limitContactClosed, isLimitNC ? ComponentKind.AuxContactNC : ComponentKind.AuxContactNO, num1, num2, drawActuator: false);

                // Ролик концевика и шток (как на фото)
                var rollerShift = (limitContactClosed != isLimitNC) ? 3.0 : 0.0;
                group.Children.Add(new Line { StartPoint = new Point(24 + rollerShift, 28), EndPoint = new Point(51 + rollerShift, 28), Stroke = stroke, StrokeThickness = 1.6, StrokeDashArray = new DoubleCollection { 3.0, 2.0 } });
                group.Children.Add(new Ellipse { Width = 14, Height = 14, Stroke = stroke, StrokeThickness = 1.8, Fill = Brushes.Transparent });
                Canvas.SetLeft(group.Children[^1], 10 + rollerShift);
                Canvas.SetTop(group.Children[^1], 21);
                break;
            case ComponentKind.ToggleSwitchNO:
            case ComponentKind.ToggleSwitchNC:
                var switchNumbers = GetContactTerminalNumbers(c);
                var switchContactKind = c.Kind == ComponentKind.ToggleSwitchNC ? ComponentKind.AuxContactNC : ComponentKind.AuxContactNO;
                DrawContactSymbol(group, stroke, contactClosed, switchContactKind, switchNumbers.Item1, switchNumbers.Item2);
                break;
            case ComponentKind.ToggleSwitchNO_NC:
            {
                var noPosition = !preview && _simulator.IsRunning && contactClosed;
                var ncSwitchGroup = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(-26, 0) };
                DrawContactSymbol(ncSwitchGroup, stroke, !noPosition, ComponentKind.AuxContactNC, "1", "2", terminalNumberX: 32);
                group.Children.Add(ncSwitchGroup);
                var noSwitchGroup = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(10, 0) };
                DrawContactSymbol(noSwitchGroup, stroke, noPosition, ComponentKind.AuxContactNO, "3", "4", terminalNumberX: 77);
                group.Children.Add(noSwitchGroup);
                break;
            }

            case ComponentKind.PushButtonNO:
            case ComponentKind.PushButtonNC:
            case ComponentKind.AuxContactNO:
            case ComponentKind.AuxContactNC:
            case ComponentKind.OverloadNC:
                var terminalNumbers = UsesSequentialContactNumbers(c) ? GetContactTerminalNumbers(c) : ("1", "2");
                DrawContactSymbol(group, stroke, contactClosed, c.Kind, terminalNumbers.Item1, terminalNumbers.Item2);
                break;
            case ComponentKind.OverloadNO:
                var overloadNoBladeShift = contactClosed ? 3.0 : 0.0;
                DrawContactSymbol(group, stroke, contactClosed, ComponentKind.AuxContactNO, "97", "98", terminalNumberX: 66);
                DrawThermalRelayContactMark(group, stroke, overloadNoBladeShift);
                break;
            case ComponentKind.ThermalRelayAuxNC:
                DrawContactSymbol(group, stroke, contactClosed, ComponentKind.AuxContactNC, "95", "96", terminalNumberX: 66);
                DrawThermalRelayContactMark(group, stroke, contactClosed ? 0 : 3);
                break;
            case ComponentKind.ThermalRelayAuxNO_NC:
            {
                var tripped = !preview && _simulator.IsRunning && contactClosed;
                var ncRelayGroup = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(-26, 0) };
                DrawContactSymbol(ncRelayGroup, stroke, !tripped, ComponentKind.AuxContactNC, "95", "96", terminalNumberX: 64);
                group.Children.Add(ncRelayGroup);
                var noRelayGroup = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(10, 0) };
                DrawContactSymbol(noRelayGroup, stroke, tripped, ComponentKind.AuxContactNO, "97", "98", terminalNumberX: 67);
                group.Children.Add(noRelayGroup);
                DrawThermalRelayPairMark(group, stroke, tripped ? 3 : 0);
                break;
            }
            case ComponentKind.PushButtonNO_NC:
            {
                var pressed = !preview && _simulator.IsRunning && contactClosed;
                var ncButton = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(-26, 0) };
                DrawContactSymbol(ncButton, stroke, !pressed, ComponentKind.PushButtonNC, "11", "12", drawActuator: false);
                group.Children.Add(ncButton);
                var noButton = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(10, 0) };
                DrawContactSymbol(noButton, stroke, pressed, ComponentKind.PushButtonNO, "13", "14", drawActuator: false);
                group.Children.Add(noButton);
                var actuatorShift = pressed ? 3.0 : 0.0;
                group.Children.Add(new Line { StartPoint = new Point(22 + actuatorShift, 28), EndPoint = new Point(58 + actuatorShift, 28), Stroke = stroke, StrokeThickness = 1.7, StrokeDashArray = new DoubleCollection { 3.0, 2.2 }, IsHitTestVisible = false });
                var actuatorLabel = new TextBlock
                {
                    Text = "E", Foreground = stroke, FontSize = 13,
                    FontStyle = FontStyle.Italic, IsHitTestVisible = false
                };
                Canvas.SetLeft(actuatorLabel, 12 + actuatorShift);
                Canvas.SetTop(actuatorLabel, 18);
                group.Children.Add(actuatorLabel);
                break;
            }
            case ComponentKind.ConContact:
            case ComponentKind.PushButtonCon:
            case ComponentKind.ToggleSwitchCon:
                var changeoverPressed = !preview && _simulator.IsRunning && contactClosed;
                DrawChangeoverContact(group, stroke, contactClosed, c.Kind == ComponentKind.PushButtonCon, changeoverPressed);
                break;
            case ComponentKind.CrossoverSwitch:
                DrawCrossoverSwitch(group, stroke, !preview && _simulator.IsRunning && contactClosed);
                break;
            case ComponentKind.AuxContactNO_NC:
                var ncPair = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(-26, 0) };
                DrawContactSymbol(ncPair, stroke, !contactClosed, ComponentKind.AuxContactNC, "11", "12");
                group.Children.Add(ncPair);
                var noPair = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(10, 0) };
                DrawContactSymbol(noPair, stroke, contactClosed, ComponentKind.AuxContactNO, "13", "14");
                group.Children.Add(noPair);
                break;
            case ComponentKind.Coil:
                group.Children.Add(new Rectangle { Width = 32, Height = 24, Stroke = stroke, StrokeThickness = 2 });
                Canvas.SetLeft(group.Children[^1], 34); Canvas.SetTop(group.Children[^1], 16);
                AddLine(group, 64, 18, 36, 38, stroke, 2);
                AddLine(group, 50, 2, 50, 16, stroke, 2);
                AddLine(group, 50, 40, 50, 62, stroke, 2);
                group.Children.Add(new TextBlock { Text = "A1", Foreground = stroke, FontSize = 8});
                Canvas.SetLeft(group.Children[^1], 36); Canvas.SetTop(group.Children[^1], 18);
                group.Children.Add(new TextBlock { Text = "A2", Foreground = stroke, FontSize = 7.9 });
                Canvas.SetLeft(group.Children[^1], 53); Canvas.SetTop(group.Children[^1], 27);
                break;
            case ComponentKind.CircuitBreaker:
            case ComponentKind.DifferentialBreaker:
                AddLine(group, 10, 28, 34, 28, stroke, 2);
                group.Children.Add(new Rectangle { Width = 32, Height = 38, Stroke = stroke, StrokeThickness = 1.8 });
                Canvas.SetLeft(group.Children[^1], 34); Canvas.SetTop(group.Children[^1], 9);
                AddLine(group, 40, 18, contactClosed ? 50 : 53, contactClosed ? 28 : 8, stroke, 2);
                if (c.Kind == ComponentKind.DifferentialBreaker)
                {
                    group.Children.Add(new TextBlock { Text = "Δ", FontSize = 15, Foreground = stroke, FontWeight = FontWeight.Bold });
                    Canvas.SetLeft(group.Children[^1], 42); Canvas.SetTop(group.Children[^1], 27);
                }
                AddLine(group, 66, 28, 90, 28, stroke, 2);
                break;
            case ComponentKind.AcSupply:
                AddLine(group, 10, 28, 33, 28, stroke, 2);
                group.Children.Add(new Ellipse { Width = 34, Height = 34, Stroke = stroke, StrokeThickness = 2 });
                Canvas.SetLeft(group.Children[^1], 33); Canvas.SetTop(group.Children[^1], 11);
                group.Children.Add(new TextBlock { Text = "~", FontSize = 22, Foreground = stroke });
                Canvas.SetLeft(group.Children[^1], 42); Canvas.SetTop(group.Children[^1], 13);
                AddLine(group, 67, 28, 90, 28, stroke, 2);
                var acInfo = new TextBlock { Text = $"{c.Phases}~ {c.Voltage}V", FontSize = 9, Foreground = stroke, TextAlignment = TextAlignment.Center, Width = 72 };
                Canvas.SetLeft(acInfo, 14); Canvas.SetTop(acInfo, 45); group.Children.Add(acInfo);
                break;
            case ComponentKind.Supply24V:
                AddLine(group, 10, 28, 40, 28, stroke, 2);
                AddLine(group, 40, 13, 40, 43, stroke, 3);
                AddLine(group, 52, 19, 52, 37, stroke, 2);
                AddLine(group, 52, 28, 90, 28, stroke, 2);
                group.Children.Add(new TextBlock { Text = "+", Foreground = stroke, FontSize = 10 });
                Canvas.SetLeft(group.Children[^1], 38); Canvas.SetTop(group.Children[^1], 1);
                group.Children.Add(new TextBlock { Text = "−", Foreground = stroke, FontSize = 10 });
                Canvas.SetLeft(group.Children[^1], 49); Canvas.SetTop(group.Children[^1], 38);
                break;
            case ComponentKind.Lamp:
            case ComponentKind.BlinkingLamp:
                var lampOn = !preview && _simulator.IsRunning && _simulator.IsEnergized(c.Id) &&
                    (c.Kind != ComponentKind.BlinkingLamp || _blinkOn);
                var sourceGlow = Color.Parse(string.IsNullOrWhiteSpace(c.GlowColorHex) ? "#FDE047" : c.GlowColorHex);
                var softenedGlow = Color.FromRgb(
                    (byte)(sourceGlow.R * 0.6 + 255 * 0.4),
                    (byte)(sourceGlow.G * 0.6 + 255 * 0.4),
                    (byte)(sourceGlow.B * 0.6 + 255 * 0.4));
                var glowBrush = new SolidColorBrush(softenedGlow);
                var lampStroke = lampOn ? glowBrush : stroke;
                Ellipse? halo = null;
                if (lampOn || c.Kind == ComponentKind.BlinkingLamp)
                {
                    var haloColor = Color.FromArgb(55, softenedGlow.R, softenedGlow.G, softenedGlow.B);
                    halo = new Ellipse
                    {
                        Width = 54,
                        Height = 54,
                        Fill = new SolidColorBrush(haloColor),
                        Effect = new DropShadowEffect { Color = softenedGlow, BlurRadius = 18, OffsetX = 0, OffsetY = 0, Opacity = 0.38 },
                        IsVisible = lampOn,
                        IsHitTestVisible = false
                    };
                    group.Children.Add(halo);
                    Canvas.SetLeft(group.Children[^1], 23);
                    Canvas.SetTop(group.Children[^1], 5);
                }
                var bulb = new Ellipse { Width = 32, Height = 32, Stroke = lampStroke, Fill = lampOn ? glowBrush : Brushes.Transparent, StrokeThickness = 2 };
                group.Children.Add(bulb);
                Canvas.SetLeft(group.Children[^1], 34); Canvas.SetTop(group.Children[^1], 16);
                var lampLines = new[]
                {
                    AddLine(group, 39, 21, 61, 43, lampStroke, 2),
                    AddLine(group, 61, 21, 39, 43, lampStroke, 2),
                    AddLine(group, 50, 0, 50, 16, lampStroke, 2),
                    AddLine(group, 50, 48, 50, 64, lampStroke, 2)
                };
                Polyline? signalLine = null;
                if (c.Kind == ComponentKind.BlinkingLamp)
                {
                    signalLine = new Polyline
                    {
                        Points = new PointCollection
                        {
                            new(76, 22), new(76, 28), new(82, 28), new(82, 34), new(76, 34), new(76, 40)
                        },
                        Stroke = lampStroke,
                        StrokeThickness = 2,
                        Fill = Brushes.Transparent
                    };
                    group.Children.Add(signalLine);
                    if (!preview && halo is not null)
                        _blinkingLampVisuals.Add(new BlinkingLampVisual(c.Id, halo, bulb, lampLines, signalLine, stroke, glowBrush));
                }
                break;
            case ComponentKind.Motor:
            case ComponentKind.DcMotor:
            case ComponentKind.AcMotor:
            case ComponentKind.AcMotor1P:
                var motorOn = !preview && _simulator.IsRunning && _simulator.IsEnergized(c.Id);
                var motorStroke = motorOn ? Brush("#16A34A") : stroke;
                if (c.Kind is ComponentKind.Motor or ComponentKind.DcMotor)
                {
                    AddLine(group, 10, 28, 31, 28, motorStroke, 2);
                    AddLine(group, 69, 28, 90, 28, motorStroke, 2);
                }
                if (motorOn)
                {
                    group.Children.Add(new Ellipse
                    {
                        Width = 42, Height = 42, Fill = Brush("#DCFCE7"),
                        Stroke = Brush("#86EFAC"), StrokeThickness = 1.4, Opacity = 0.72
                    });
                    Canvas.SetLeft(group.Children[^1], 29); Canvas.SetTop(group.Children[^1], 7);
                }
                if (c.Kind == ComponentKind.AcMotor)
                {
                    AddLine(group, 26, 3, 36, 14, motorStroke, 2);
                    AddLine(group, 50, -2, 50, 9, motorStroke, 2);
                    AddLine(group, 74, 3, 64, 14, motorStroke, 2);
                }
                else if (c.Kind == ComponentKind.AcMotor1P)
                {
                    AddLine(group, 26, 3, 36, 14, motorStroke, 2);
                    AddLine(group, 74, 3, 64, 14, motorStroke, 2);
                }
                group.Children.Add(new Ellipse { Width = 38, Height = 38, Stroke = motorStroke, StrokeThickness = 2 });
                Canvas.SetLeft(group.Children[^1], 31); Canvas.SetTop(group.Children[^1], 9);
                group.Children.Add(new TextBlock
                    {
                        Text = "M",
                        Foreground = motorStroke,
                        FontSize = 16,
                        FontWeight = FontWeight.SemiBold,
                        Tag = "NoZoom"
                    });
                Canvas.SetLeft(group.Children[^1], 42); Canvas.SetTop(group.Children[^1], 13);
                group.Children.Add(new TextBlock { Text = c.Kind == ComponentKind.AcMotor ? "3~" : c.Kind == ComponentKind.AcMotor1P ? "1~" : "—", Foreground = motorStroke, FontSize = 10, FontWeight = FontWeight.Bold, Tag = "NoZoom" });
                Canvas.SetLeft(group.Children[^1], 44); Canvas.SetTop(group.Children[^1], 32);
                if (motorOn)
                {
                    var clockwise = c.Kind == ComponentKind.AcMotor
                        ? _simulator.IsMotorClockwise(c.Id)
                        : c.Rotation % 180 == 0;
                    var startX = clockwise ? 39.0 : 61.0;
                    var tipX = clockwise ? 61.0 : 39.0;
                    var arc = Geometry.Parse(FormattableString.Invariant($"M {startX},62 Q 50,44 {tipX},62"));
                    group.Children.Add(new Avalonia.Controls.Shapes.Path { Data = arc, Stroke = motorStroke, StrokeThickness = 2.2, Fill = Brushes.Transparent });
                    var tdx = tipX - 50.0;
                    var tdy = 62.0 - 44.0;
                    var tangentLength = Math.Sqrt(tdx * tdx + tdy * tdy);
                    tdx /= tangentLength;
                    tdy /= tangentLength;
                    var pdx = -tdy;
                    var pdy = tdx;
                    var headBaseX = tipX - tdx * 5;
                    var headBaseY = 62.0 - tdy * 5;
                    AddLine(group, tipX, 62, headBaseX + pdx * 3, headBaseY + pdy * 3, motorStroke, 2.2);
                    AddLine(group, tipX, 62, headBaseX - pdx * 3, headBaseY - pdy * 3, motorStroke, 2.2);
                }
                break;
        }

        if (c.Kind == ComponentKind.ThermalRelay && !preview && _simulator.IsRunning && _simulator.IsThermalRelayTripped(c.Id))
        {
            group.Children.Add(new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = Brush("#EF4444"),
                Stroke = Brush("#B91C1C"),
                StrokeThickness = 0.8,
                IsHitTestVisible = false
            });
            Canvas.SetLeft(group.Children[^1], 89);
            Canvas.SetTop(group.Children[^1], 20);
        }

        SetSymbolStrokeThickness(group, 1.4);

        var symbolScaleX = c.Kind switch
        {
            ComponentKind.Rcd3PN => ComponentTerminals.PoleSpacing / 32.0,
            ComponentKind.ThermalRelay or ComponentKind.Contactor2P or ComponentKind.Contactor3P or ComponentKind.Contactor4P => 1.0,
            ComponentKind.Breaker1P or ComponentKind.Breaker1PN or ComponentKind.Breaker2P or ComponentKind.Breaker3P or ComponentKind.Breaker3PN or ComponentKind.Rcd1PN or ComponentKind.SurgeProtector1P or ComponentKind.SurgeProtector3PN => ComponentTerminals.PoleSpacing / 50.0,
            _ => 1.0
        };
        var symbolTransform = new TransformGroup();
        if (symbolScaleX != 1.0) symbolTransform.Children.Add(ScaleAbout(symbolScaleX, 1, 50, 32));
        symbolTransform.Children.Add(RotateAbout(c.Rotation, 50, 32));
        group.RenderTransform = symbolTransform;
        group.RenderTransformOrigin = RelativePoint.TopLeft;
        Canvas.SetLeft(group, c.X - 50); Canvas.SetTop(group, c.Y - 32); SchematicCanvas.Children.Add(group);
        if (c.Kind is ComponentKind.Breaker1P or ComponentKind.Breaker1PN or ComponentKind.Breaker2P or ComponentKind.Breaker3P or ComponentKind.Breaker3PN or ComponentKind.Rcd1PN or ComponentKind.Rcd3PN or ComponentKind.ThermalRelay or ComponentKind.Contactor2P or ComponentKind.Contactor3P or ComponentKind.Contactor4P)
        {
            var poleCount = c.Kind switch
            {
                ComponentKind.Breaker1P => 1,
                ComponentKind.Breaker1PN or ComponentKind.Breaker2P => 2,
                ComponentKind.Rcd1PN => 2,
                ComponentKind.Rcd3PN => 4,
                ComponentKind.ThermalRelay => 3,
                ComponentKind.Contactor2P => 2,
                ComponentKind.Contactor3P => 3,
                ComponentKind.Contactor4P => 4,
                ComponentKind.Breaker3P => 3,
                _ => 4
            };
            var poleSpacing = ComponentTerminals.PoleSpacing;
            var lastPoleCenter = 50 + poleSpacing * (poleCount - 1) / 2;
            if (c.Kind is not (ComponentKind.ThermalRelay or ComponentKind.Contactor2P or ComponentKind.Contactor3P or ComponentKind.Contactor4P))
                AddCanvasLabel(c.Tag, c.X + (poleCount == 1 ? 27 : lastPoleCenter - 50 + 24), c.Y - 8, 9);
            for (var pole = 0; pole < poleCount; pole++)
            {
                var x = 50 - poleSpacing * (poleCount - 1) / 2 + poleSpacing * pole;
                var worldX = c.X + x - 50;
                AddCanvasLabel((pole * 2 + 1).ToString(), worldX + 5, c.Y - 35, 8);
                AddCanvasLabel((pole * 2 + 2).ToString(), worldX + 5, c.Y + 25, 8);
            }
        }
        if (c.Kind is ComponentKind.AuxContactNO_NC or ComponentKind.OverloadNO or ComponentKind.ThermalRelayAuxNC or ComponentKind.ThermalRelayAuxNO_NC)
            AddCanvasLabel($"-{c.Tag}", c.X - (c.Kind == ComponentKind.ThermalRelayAuxNO_NC ? 46 : 49),
                c.Y + (c.Kind == ComponentKind.ThermalRelayAuxNO_NC ? 5 : -5), 9);
        else if (c.Kind is ComponentKind.ConContact or ComponentKind.PushButtonCon or ComponentKind.ToggleSwitchCon)
            AddCanvasLabel(c.Tag, c.X + 30, c.Y - 5, 9);
        else if (c.Kind is ComponentKind.ToggleSwitchNO or ComponentKind.ToggleSwitchNC or ComponentKind.ToggleSwitchNO_NC)
            AddCanvasLabel(c.Tag, c.X + (c.Kind == ComponentKind.ToggleSwitchNO_NC ? 43 : 25), c.Y - 25, 9);
        else if (c.Kind == ComponentKind.CrossoverSwitch)
            AddCanvasLabel(c.Tag, c.X + 30, c.Y - 5, 9);
        else if (c.Kind == ComponentKind.PushButtonNO_NC)
            AddCanvasLabel(c.Tag, c.X + 25, c.Y - 8, 9);
        else if (c.Kind is ComponentKind.PushButtonNO or ComponentKind.PushButtonNC or ComponentKind.ToggleSwitchNO or ComponentKind.ToggleSwitchNC or ComponentKind.AuxContactNO or ComponentKind.AuxContactNC or ComponentKind.AuxContactNO_NC or ComponentKind.OverloadNC)
            AddCanvasLabel(c.Tag, c.X + 19, c.Y - 8, 9);
        if (c.Kind is ComponentKind.Lamp or ComponentKind.BlinkingLamp)
            AddCanvasLabel(c.Tag, c.X - 32, c.Y - 5, 12);
        if (c.Kind == ComponentKind.DcMotor)
            AddCanvasLabel(c.Tag, c.X - 3, c.Y + 20, 9);
        else if (c.Kind is ComponentKind.AcMotor or ComponentKind.AcMotor1P)
            AddCanvasLabel(c.Tag, c.X - 44, c.Y - 4, 9);
        else if (c.Kind == ComponentKind.Coil)
            AddCanvasLabel(c.Tag, c.X + 24, c.Y - 24, 9);
        foreach (var terminal in ComponentTerminals.Get(c))
        {
            var p = TerminalPosition(c, terminal.Id);
            DrawTerminal(p.X, p.Y);
            if (c.Kind == ComponentKind.AcSupply && c.Phases == 3)
            {
                var termLabel = new TextBlock { Text = terminal.Label, FontSize = FontSizeForZoom(8), Foreground = _darkTheme ? Brushes.White : Brush("#475569"), IsHitTestVisible = false };
                Canvas.SetLeft(termLabel, p.X < c.X ? p.X - 16 : p.X + 4); Canvas.SetTop(termLabel, p.Y - 14); SchematicCanvas.Children.Add(termLabel);
            }
        }
    }

    private void AddCanvasLabel(string text, double x, double y, double fontSize)
    {
        var label = new TextBlock { Text = text, FontSize = FontSizeForZoom(fontSize), Foreground = _darkTheme ? Brushes.White : Brushes.Black, IsHitTestVisible = false };
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y); SchematicCanvas.Children.Add(label);
    }

    

private double FontSizeForZoom(double baseSize)
{
    return baseSize;
}

private void ApplyZoomToSchematicText(Panel panel)
{
    foreach (var child in panel.Children)
    {
        if (child is TextBlock text)
        {
            // Не изменяем размер внутренних надписей символов.
            if (text.Tag?.ToString() == "NoZoom")
                continue;

            // Сохраняем исходный размер только один раз.
            if (text.Tag is not double baseFontSize)
            {
                baseFontSize = text.FontSize;
                text.Tag = baseFontSize;
            }

            var screenSize = Math.Clamp(
                baseFontSize * Math.Pow(_zoom, -0.03),
                9,
                20
            );

            text.FontSize = screenSize / Math.Max(0.01, _zoom);
        }
        else if (child is Panel nestedPanel)
        {
            // Обрабатываем также вложенные Canvas.
            ApplyZoomToSchematicText(nestedPanel);
        }
    }
}


    private void DrawPowerMarker(Canvas group, ComponentKind kind, IBrush stroke, int rotation)
    {
        var labels = ComponentTerminals.GetPowerConductorLabels(kind, rotation);
        if (labels.Count == 0) return;

        var step = labels.Count switch
        {
            1 => 0.0,
            _ => ComponentTerminals.PoleSpacing
        };
        var firstY = 32.0 - step * (labels.Count - 1) / 2.0;
        const double labelRightShift = 7;
        var rotationRadians = rotation * Math.PI / 180.0;
        var labelShiftX = labelRightShift * Math.Cos(rotationRadians);
        var labelShiftY = -labelRightShift * Math.Sin(rotationRadians);

        for (var i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            var y = firstY + i * step;

            var labelText = new TextBlock
            {
                Text = kind == ComponentKind.PowerDcEarth && label == "PE" ? "" : label,
                Foreground = stroke,
                FontSize = FontSizeForZoom(label.Length > 2 ? 9 : 10),
                FontWeight = FontWeight.SemiBold,
                Width = 22,
                TextAlignment = TextAlignment.Left
            };
            if (rotation != 0)
            {
                labelText.RenderTransform = new RotateTransform(-rotation);
                labelText.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            }
            Canvas.SetLeft(labelText, 2 + labelShiftX);
            Canvas.SetTop(labelText, y - 7 + labelShiftY);
            group.Children.Add(labelText);

            if (label == "PE")
            {
                // Protective-earth symbol: short vertical stem and three decreasing bars.
                AddLine(group, 27, y - 5, 27, y - 1, stroke, 1.5);
                AddLine(group, 21, y - 1, 33, y - 1, stroke, 1.5);
                AddLine(group, 23, y + 2, 31, y + 2, stroke, 1.5);
                AddLine(group, 25, y + 5, 29, y + 5, stroke, 1.5);
                AddLine(group, 36, y, 90, y, stroke, 2.0);
            }
            else
            {
                AddLine(group, 27, y, 90, y, stroke, 2.0);
            }
        }
    }

    private void DrawMultiPoleDevice(Canvas group, ComponentKind kind, IBrush stroke, bool closed, string tag,
        IReadOnlySet<int>? conductingPoles = null)
    {
        if (kind is ComponentKind.Contactor2P or ComponentKind.Contactor3P or ComponentKind.Contactor4P)
        {
            var poles = kind == ComponentKind.Contactor2P ? 2 : kind == ComponentKind.Contactor3P ? 3 : 4;
            var contactorSpacing = ComponentTerminals.PoleSpacing;
            var contactorFirstX = 50 - contactorSpacing * (poles - 1) / 2;

            // Mechanical link and main poles share the same open-contact shape
            // for all pole counts; the coil with the matching tag closes them.
            for (var pair = 0; pair < poles - 1; pair++)
            {
                var pairFirstX = contactorFirstX + contactorSpacing * pair;
                var pairLastX = pairFirstX + contactorSpacing;
                var linkShift = closed ? 6.0 : 0.0;
                group.Children.Add(new Line { StartPoint = new Point(pairFirstX - 4 + linkShift, 32), EndPoint = new Point(pairLastX - 25 + linkShift, 32), Stroke = stroke, StrokeThickness = 1 });
                group.Children.Add(new Line { StartPoint = new Point(pairFirstX + 15 + linkShift, 32), EndPoint = new Point(pairLastX - 4 + linkShift, 32), Stroke = stroke, StrokeThickness = 1 });
            }
            group.Children.Add(new TextBlock
            {
                Text = tag.StartsWith('-') ? tag : $"-{tag}",
                Foreground = _darkTheme ? Brushes.White : Brushes.Black,
                FontSize = FontSizeForZoom(8)
            });
            Canvas.SetLeft(group.Children[^1], contactorFirstX - 32);
            Canvas.SetTop(group.Children[^1], 26);

            for (var pole = 0; pole < poles; pole++)
            {
                var x = contactorFirstX + contactorSpacing * pole;
                var contactStroke = conductingPoles?.Contains(pole) == true ? Brush("#16A34A") : stroke;
                AddLine(group, x, 0, x, 20, contactStroke, 1.5);
                DrawSwitchBlade(group, x, 20, 44, contactStroke, closed, 8, 1.5);
                AddLine(group, x, 44, x, 64, contactStroke, 1.5);
            }
            return;
        }

        if (kind == ComponentKind.ThermalRelay)
        {
            // Three-pole thermal relay with a shared rectangular housing and
            // one thermal element per phase, matching the supplied reference.
            const double poleSpacing = 28;
            const double firstPoleX = 22;
            const double housingTop = 18;
            const double housingBottom = 46;
            const double housingLeft = 9;
            const double housingWidth = 87;

            var relayDesignation = tag.StartsWith('-') ? tag : $"-{tag}";
            group.Children.Add(new TextBlock
            {
                Text = relayDesignation,
                Foreground = stroke,
                FontSize = FontSizeForZoom(8)
            });
            Canvas.SetLeft(group.Children[^1], -25);
            Canvas.SetTop(group.Children[^1], 24);
            AddLine(group, -4, 32, housingLeft, 32, stroke, 1.4);

            group.Children.Add(new Rectangle
            {
                Width = housingWidth,
                Height = housingBottom - housingTop,
                Stroke = stroke,
                StrokeThickness = 1.4,
                Fill = Brushes.Transparent
            });
            Canvas.SetLeft(group.Children[^1], housingLeft);
            Canvas.SetTop(group.Children[^1], housingTop);

            // Three adjacent chambers represent the relay's phase elements.
           

            for (var pole = 0; pole < 3; pole++)
            {
                var x = firstPoleX + poleSpacing * pole;
                // The phase conductor follows the stepped thermal element
                // inside the housing: down, right, down, left, and down.
                group.Children.Add(new Polyline
                {
                    Points = new PointCollection
                    {
                        new(x, 0), new(x, 25), new(x + 9, 25),
                        new(x + 9, 39),
                        new(x, 39), new(x, 64)
                    },
                    Stroke = stroke,
                    StrokeThickness = 1.4,
                });
            }
            return;
        }

        if (kind == ComponentKind.Rcd3PN)
        {
            // Four-pole RCD follows the same IEC-style symbol as the 1P+N
            // version: thermal detector at the left, linked knife contacts,
            // straight lower terminals, and one shared differential core.
            const double rcdPoleSpacing = 32;
            const int poleCount = 4;
            var rcdFirstX = 50 - rcdPoleSpacing * (poleCount - 1) / 2;

            var rcdDetectorX = rcdFirstX - 41;
            AddLine(group, rcdDetectorX - 6, 0, rcdDetectorX - 6, 29, stroke, 1.5);
            AddLine(group, rcdDetectorX - 6, 14, rcdDetectorX, 14, stroke, 1.2);
            group.Children.Add(new Rectangle { Width = 22, Height = 15, Stroke = stroke, StrokeThickness = 1.2 });
            Canvas.SetLeft(group.Children[^1], rcdDetectorX); Canvas.SetTop(group.Children[^1], 7);
            AddLine(group, rcdDetectorX + 11, 7, rcdDetectorX + 11, 22, stroke, 1.0);
            AddLine(group, rcdDetectorX, 14.5, rcdDetectorX + 22, 14.5, stroke, 1.0);
            AddLine(group, rcdDetectorX + 22, 14, rcdFirstX, 14, stroke, 1.2);
            AddLine(group, rcdDetectorX + 11, 22, rcdDetectorX + 11, 27, stroke, 1.2);
            AddLine(group, rcdDetectorX + 11, 27, rcdDetectorX + 19, 27, stroke, 1.2);

            for (var pole = 0; pole < poleCount; pole++)
            {
                var x = rcdFirstX + rcdPoleSpacing * pole;
                AddLine(group, x, 0, x, 17, stroke, 1.5);
                DrawSwitchBlade(group, x, 17, 50, stroke, closed, 12, 1.5);
                AddLine(group, x, 50, x, 64, stroke, 1.5);
            }

            group.Children.Add(new Ellipse
            {
                Width = rcdPoleSpacing * (poleCount - 1) + 12,
                Height = 14,
                Stroke = stroke,
                StrokeThickness = 1.1,
                Fill = Brushes.Transparent
            });
            Canvas.SetLeft(group.Children[^1], rcdFirstX - 6);
            Canvas.SetTop(group.Children[^1], 42);
            group.Children.Add(new TextBlock { Text = "L", Foreground = stroke, FontSize = 7 });
            Canvas.SetLeft(group.Children[^1], rcdFirstX - 29);
            Canvas.SetTop(group.Children[^1], 41);
            return;
        }

        if (kind == ComponentKind.Rcd1PN)
        {
            // Layout follows the hand sketch: left thermal unit, two open
            // contacts above a shared oval core and straight lower terminals.
            AddLine(group, -22, 0, -22, 29, stroke, 1.5);
            AddLine(group, -22, 14, -16, 14, stroke, 1.2);
            group.Children.Add(new Rectangle { Width = 22, Height = 15, Stroke = stroke, StrokeThickness = 1.2 });
            Canvas.SetLeft(group.Children[^1], -16); Canvas.SetTop(group.Children[^1], 7);
            AddLine(group, -5, 7, -5, 22, stroke, 1.0);
            AddLine(group, -16, 14.5, 6, 14.5, stroke, 1.0);
            // Restore the connector removed in the previous edit.
            AddLine(group, 6, 14, 25, 14, stroke, 1.2);
            // Keep the under-block elbow at its original centered position.
            AddLine(group, -5, 22, -5, 27, stroke, 1.2);
            AddLine(group, -5, 27, 3, 27, stroke, 1.2);

            // Top terminal stubs, separated open blades and vertical lower legs.
            AddLine(group, 25, 0, 25, 17, stroke, 1.5);
            DrawSwitchBlade(group, 25, 17, 50, stroke, closed, 12, 1.5);
            AddLine(group, 25, 50, 25, 64, stroke, 1.5);
            AddLine(group, 75, 0, 75, 17, stroke, 1.5);
            DrawSwitchBlade(group, 75, 17, 50, stroke, closed, 12, 1.5);
            AddLine(group, 75, 50, 75, 64, stroke, 1.5);

            group.Children.Add(new Ellipse { Width = 60, Height = 14, Stroke = stroke, StrokeThickness = 1.1, Fill = Brushes.Transparent });
            Canvas.SetLeft(group.Children[^1], 20); Canvas.SetTop(group.Children[^1], 42);
            group.Children.Add(new TextBlock { Text = "L", Foreground = stroke, FontSize = 7 });
            Canvas.SetLeft(group.Children[^1], -4); Canvas.SetTop(group.Children[^1], 41);
            return;
        }
        if (kind == ComponentKind.Breaker1P)
        {
            AddLine(group, 50, 0, 50, 17, stroke, 1.7);
            DrawSwitchBlade(group, 50, 17, 50, stroke, closed, 7, 1.7);
            // Moving contact is physically continuous with the straight lower terminal.
            AddLine(group, 50, 50, 50, 64, stroke, 1.7);
            // Thermal trip, magnetic arrow and both links meet the blade exactly.
            DrawBreakerReleaseGlyph(group, 50, closed, 7, stroke);
            return;
        }
        var count = kind switch
        {
            ComponentKind.Breaker1P or ComponentKind.SurgeProtector1P => 1,
            ComponentKind.Breaker1PN or ComponentKind.Breaker2P or ComponentKind.Rcd1PN => 2,
            ComponentKind.Breaker3P => 3,
            _ => 4
        };
        var spacing = 50.0;
        var firstX = 50 - spacing * (count - 1) / 2;
        var isRcd = kind is ComponentKind.Rcd1PN or ComponentKind.Rcd3PN;
        var isThermal = kind == ComponentKind.ThermalRelay;
        var isSurge = kind is ComponentKind.SurgeProtector1P or ComponentKind.SurgeProtector3PN;
        var isMultiBreaker = kind is ComponentKind.Breaker1PN or ComponentKind.Breaker2P or ComponentKind.Breaker3P or ComponentKind.Breaker3PN;
        if (isRcd || isSurge)
        {
            group.Children.Add(new Rectangle { Width = count * spacing - 4, Height = 52, Stroke = stroke, StrokeThickness = 1.4 });
            Canvas.SetLeft(group.Children[^1], firstX - 10); Canvas.SetTop(group.Children[^1], 6);
            if (isRcd)
            {
                var detectorX = firstX - 31;
                group.Children.Add(new Rectangle { Width = 12, Height = 12, Stroke = stroke, StrokeThickness = 1.3 });
                Canvas.SetLeft(group.Children[^1], detectorX); Canvas.SetTop(group.Children[^1], 26);
                AddLine(group, detectorX + 3, 26, detectorX + 3, 38, stroke, 1);
                AddLine(group, detectorX + 8, 26, detectorX + 8, 38, stroke, 1);
                AddLine(group, detectorX, 32, detectorX + 12, 32, stroke, 1);
                group.Children.Add(new Line { StartPoint = new Point(detectorX + 12, 32), EndPoint = new Point(firstX - 10, 32), Stroke = stroke, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 2 } });
                group.Children.Add(new TextBlock { Text = "R", Foreground = stroke, FontSize = 8 });
                Canvas.SetLeft(group.Children[^1], detectorX + 3); Canvas.SetTop(group.Children[^1], 39);
            }
        }
        for (var i = 0; i < count; i++)
        {
            var x = firstX + i * spacing;
            AddLine(group, x, 0, x, 17, stroke, 1.7);
            if (isSurge)
            {
                AddLine(group, x - 5, 25, x + 5, 37, stroke, 1.5);
                AddLine(group, x + 5, 25, x - 5, 37, stroke, 1.5);
            }
            else if (isThermal)
            {
                // Each thermal pole is one continuous stepped conductor: down,
                // right, down, left, then down to the lower terminal.
                group.Children.Add(new Polyline
                {
                    Points = new PointCollection { new(x, 0), new(x, 25), new(x + 10, 25), new(x + 10, 39), new(x, 39), new(x, 64) },
                    Stroke = stroke,
                    StrokeThickness = 1.7
                });
            }
            else
            {
                DrawSwitchBlade(group, x, 17, 50, stroke, closed, 7, 1.7);
                var neutralPole = i == count - 1 && kind is ComponentKind.Breaker1PN or ComponentKind.Breaker3PN;
                if (isMultiBreaker && !neutralPole)
                {
                    // Repeat the complete 1P breaker glyph in every pole: stepped
                    // thermal release, magnetic triangle and its connecting arm.
                    DrawBreakerReleaseGlyph(group, x, closed, 7, stroke);
                }
            }
            if (!isThermal) AddLine(group, x, 50, x, 64, stroke, 1.7);
        }
    }

    private static void DrawSwitchBlade(Canvas group, double x, double upperY, double lowerY,
        IBrush stroke, bool closed, double openOffset, double thickness)
    {
        if (closed)
        {
            AddLine(group, x, upperY, x + 6, upperY, stroke, thickness);
            AddLine(group, x + 6, upperY, x, lowerY, stroke, thickness);
        }
        else
            AddLine(group, x - openOffset, upperY, x, lowerY, stroke, thickness);
    }

    private static void DrawBreakerReleaseGlyph(Canvas group, double x, bool closed, double openOffset, IBrush stroke)
    {
        // The closed blade has a short horizontal section at its top; its
        // slanted section therefore starts at x + 6. The open blade starts at
        // x - openOffset. Intersect each release link with that actual slant.
        double BladeXAt(double y)
        {
            var bladeTopX = closed ? x + 6 : x - openOffset;
            return bladeTopX + (x - bladeTopX) * ((y - 17) / (50 - 17));
        }

        var bladeAtTrip = BladeXAt(28);
        var bladeAtMagnet = BladeXAt(37);
        var linkageShift = closed ? 4.0 : 0.0;
        group.Children.Add(new Polyline
        {
            Points = new PointCollection
            {
                new(x - 40 + linkageShift, 41), new(x - 28 + linkageShift, 37), new(x - 31 + linkageShift, 28),
                new(x - 19 + linkageShift, 24), new(x - 16 + linkageShift, 32), new(bladeAtTrip, 28)
            },
            Stroke = stroke, StrokeThickness = 1.5
        });
        // Keep the original curve and arrow shape, with each endpoint placed
        // directly on the blade. No separate connector segments are needed.
        group.Children.Add(new Line { StartPoint = new Point(x - 30 + linkageShift, 46), EndPoint = new Point(bladeAtMagnet, 37), Stroke = stroke, StrokeThickness = 1.5 });
        group.Children.Add(new Polygon
        {
            Points = new PointCollection { new(x - 36 + linkageShift, 49), new(x - 31 + linkageShift, 42), new(x - 28 + linkageShift, 50) },
            Fill = stroke, Stroke = stroke, StrokeThickness = 0.8
        });
    }

    private static void DrawContactSymbol(
        Canvas group,
        IBrush stroke,
        bool closed,
        ComponentKind kind,
        string upperNumber,
        string lowerNumber,
        bool drawActuator = true,
        double contactThickness = 2.2,
        bool showTerminalNumbers = true,
        double terminalNumberX = 39)
    {
        bool isPushButton = kind is ComponentKind.PushButtonNO or ComponentKind.PushButtonNC;
        bool isPressed = kind == ComponentKind.PushButtonNC ? !closed : closed;

        // Match the supplied reference drawing:
        // upper fixed contact on the right, slanted moving blade on the left,
        // lower vertical terminal, and a horizontal dashed E actuator on the left.
        AddLine(group, 58, 2, 58, 21, stroke, contactThickness);
        AddLine(group, 58, 38, 58, 62, stroke, contactThickness);

        var isNormallyClosed = kind is ComponentKind.PushButtonNC or ComponentKind.AuxContactNC
            or ComponentKind.ToggleSwitchNC or ComponentKind.OverloadNC;
        if (isNormallyClosed)
        {
            // Keep the NC blade fixed; pressing/opening it only removes the bridge.
            AddLine(group, 64, 21, 58, 38, stroke, contactThickness);
            if (closed) AddLine(group, 58, 21, 64, 21, stroke, contactThickness);
        }
        else if (closed)
        {
            // A closed NO uses the same appearance as a closed NC.
            AddLine(group, 64, 21, 58, 38, stroke, contactThickness);
            AddLine(group, 58, 21, 64, 21, stroke, contactThickness);
        }
        else
            AddLine(group, 46, 20, 58, 38, stroke, contactThickness);

        if (isPushButton && drawActuator)
        {
            // Pressing the button nudges the dashed actuator toward the contact.
            var actuatorShift = isPressed ? 3.0 : 0.0;
            group.Children.Add(new Line { StartPoint = new Point(22 + actuatorShift, 28), EndPoint = new Point(51 + actuatorShift, 28), Stroke = stroke, StrokeThickness = 1.7, StrokeDashArray = new DoubleCollection { 3.0, 2.2 }, IsHitTestVisible = false });

            var eLabel = new TextBlock
            {
                Text = "E",
                Foreground = stroke,
                FontSize = 13,
                FontStyle = FontStyle.Italic
            };
            Canvas.SetLeft(eLabel, 12 + actuatorShift);
            Canvas.SetTop(eLabel, 18);
            group.Children.Add(eLabel);
        }

        if (showTerminalNumbers)
        {
            // Terminal numbers are on the left of the vertical conductor, as in the reference.
            var upper = new TextBlock
            {
                Text = upperNumber,
                Foreground = stroke,
                FontSize = 10
            };
            Canvas.SetLeft(upper, terminalNumberX);
            Canvas.SetTop(upper, 3);
            group.Children.Add(upper);

            var lower = new TextBlock
            {
                Text = lowerNumber,
                Foreground = stroke,
                FontSize = 10
            };
            Canvas.SetLeft(lower, terminalNumberX);
            Canvas.SetTop(lower, 52);
            group.Children.Add(lower);
        }
    }

    private static void DrawToggleSwitchSymbol(
        Canvas group,
        IBrush stroke,
        bool closed,
        string leftNumber,
        string rightNumber,
        double centerX = 50,
        double terminalHalfSpan = 40,
        double leverTopY = 0)
    {
        const double contactY = 32;
        const double contactRadius = 3;
        var contactOffset = terminalHalfSpan / 2;
        var leftTerminal = centerX - terminalHalfSpan;
        var rightTerminal = centerX + terminalHalfSpan;
        var leftContact = centerX - contactOffset;
        var rightContact = centerX + contactOffset;
        AddLine(group, leftTerminal, contactY, leftContact - contactRadius, contactY, stroke, 2.2);
        AddLine(group, rightContact + contactRadius, contactY, rightTerminal, contactY, stroke, 2.2);
        foreach (var x in new[] { leftContact, rightContact })
        {
            group.Children.Add(new Ellipse { Width = contactRadius * 2, Height = contactRadius * 2, Stroke = stroke, StrokeThickness = 2, Fill = Brushes.White });
            Canvas.SetLeft(group.Children[^1], x - contactRadius);
            Canvas.SetTop(group.Children[^1], contactY - contactRadius);
        }

        var bridgeY = closed ? 35.0 : 20.0;
        AddLine(group, leftContact, bridgeY, rightContact, bridgeY, stroke, 2.2);
        AddLine(group, centerX, leverTopY, centerX, bridgeY, stroke, 2.2);
        AddTerminalNumber(group, leftNumber, leftTerminal - 5, 43, stroke);
        AddTerminalNumber(group, rightNumber, rightTerminal - 3, 43, stroke);
    }

    private static void DrawThermalRelayContactMark(Canvas group, IBrush stroke, double shift)
    {
        AddLine(group, 33 + shift, 20, 39 + shift, 20, stroke, 1.8);
        AddLine(group, 33 + shift, 20, 33 + shift, 29, stroke, 1.8);
        AddLine(group, 39 + shift, 20, 39 + shift, 29, stroke, 1.8);
        group.Children.Add(new Line { StartPoint = new Point(39 + shift, 24), EndPoint = new Point(58 + shift, 24), Stroke = stroke, StrokeThickness = 1.6, StrokeDashArray = new DoubleCollection { 2.0, 2.0 }, IsHitTestVisible = false });
        AddLine(group, 71, 22, 71, 29, stroke, 2.2);
        AddLine(group, 71, 29, 78, 29, stroke, 2.2);
        AddLine(group, 78, 29, 78, 36, stroke, 2.2);
        AddLine(group, 78, 36, 71, 36, stroke, 2.2);
        AddLine(group, 71, 36, 71, 43, stroke, 2.2);
    }

    private static void DrawThermalRelayPairMark(Canvas group, IBrush stroke, double shift)
    {
        AddLine(group, 7 + shift, 25, 7 + shift, 34, stroke, 1.8);
        AddLine(group, 7 + shift, 25, 13 + shift, 25, stroke, 1.8);
        AddLine(group, 13 + shift, 25, 13 + shift, 34, stroke, 1.8);
        group.Children.Add(new Line { StartPoint = new Point(13 + shift, 29), EndPoint = new Point(59 + shift, 29), Stroke = stroke, StrokeThickness = 1.6, StrokeDashArray = new DoubleCollection { 2.0, 2.0 }, IsHitTestVisible = false });
        AddLine(group, 81, 22, 81, 29, stroke, 2.2);
        AddLine(group, 81, 29, 88, 29, stroke, 2.2);
        AddLine(group, 88, 29, 88, 36, stroke, 2.2);
        AddLine(group, 88, 36, 81, 36, stroke, 2.2);
        AddLine(group, 81, 36, 81, 43, stroke, 2.2);
    }

    private static void DrawCrossoverSwitch(Canvas group, IBrush stroke, bool crossed)
    {
        const double leftContactX = 25;
        const double rightContactX = 75;
        const double upperY = 20;
        const double lowerY = 44;
        AddLine(group, 10, upperY, 22, upperY, stroke, 2.2);
        AddLine(group, 10, lowerY, 22, lowerY, stroke, 2.2);
        AddLine(group, 78, upperY, 90, upperY, stroke, 2.2);
        AddLine(group, 78, lowerY, 90, lowerY, stroke, 2.2);
        foreach (var point in new[] { new Point(leftContactX, upperY), new Point(leftContactX, lowerY), new Point(rightContactX, upperY), new Point(rightContactX, lowerY) })
        {
            group.Children.Add(new Ellipse { Width = 6, Height = 6, Stroke = stroke, StrokeThickness = 2, Fill = Brushes.White });
            Canvas.SetLeft(group.Children[^1], point.X - 3);
            Canvas.SetTop(group.Children[^1], point.Y - 3);
        }
        if (!crossed)
        {
            AddLine(group, leftContactX + 3, upperY, rightContactX - 3, upperY, stroke, 2.2);
            AddLine(group, leftContactX + 3, lowerY, rightContactX - 3, lowerY, stroke, 2.2);
        }
        else
        {
            AddLine(group, leftContactX + 3, upperY, rightContactX - 3, lowerY, stroke, 2.2);
            AddLine(group, leftContactX + 3, lowerY, rightContactX - 3, upperY, stroke, 2.2);
        }
        AddLine(group, 50, 2, 50, 12, stroke, 2.2);
        AddLine(group, 40, 12, 60, 12, stroke, 2.2);
        AddTerminalNumber(group, "1", 6, 12, stroke);
        AddTerminalNumber(group, "2", 6, 48, stroke);
        AddTerminalNumber(group, "3", 87, 12, stroke);
        AddTerminalNumber(group, "4", 87, 48, stroke);
    }

    private static void DrawChangeoverContact(Canvas group, IBrush stroke, bool energized, bool isButton = false, bool pressed = false)
    {
        AddLine(group, 50, 2, 50, 20, stroke, 2.2);
        AddLine(group, 26, 38, 26, 62, stroke, 2.2);
        AddLine(group, 74, 38, 74, 62, stroke, 2.2);
        AddLine(group, 26, 38, 35, 38, stroke, 2.2);
        AddLine(group, 65, 38, 74, 38, stroke, 2.2);
        AddLine(group, 50, 20, energized ? 65 : 35, 38, stroke, 2.2);

        if (isButton)
        {
            var actuatorShift = pressed ? 3.0 : 0.0;
            group.Children.Add(new Line { StartPoint = new Point(13 + actuatorShift, 28), EndPoint = new Point(43 + actuatorShift, 28), Stroke = stroke, StrokeThickness = 1.7, StrokeDashArray = new DoubleCollection { 3.0, 2.2 }, IsHitTestVisible = false });
            var eLabel = new TextBlock { Text = "E", Foreground = stroke, FontSize = 13, FontStyle = FontStyle.Italic, IsHitTestVisible = false };
            Canvas.SetLeft(eLabel, 3 + actuatorShift);
            Canvas.SetTop(eLabel, 18);
            group.Children.Add(eLabel);
        }

        AddTerminalNumber(group, "11", 54, 0, stroke);
        AddTerminalNumber(group, "12", 8, 39, stroke);
        AddTerminalNumber(group, "14", 77, 39, stroke);
    }

    private static void AddTerminalNumber(Canvas group, string number, double x, double y, IBrush stroke)
    {
        var label = new TextBlock { Text = number, Foreground = stroke, FontSize = 8, IsHitTestVisible = false };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        group.Children.Add(label);
    }

    private static Line AddLine(Canvas canvas, double x1, double y1, double x2, double y2, IBrush stroke, double thickness)
    {
        var line = new Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = stroke, StrokeThickness = thickness };
        canvas.Children.Add(line);
        return line;
    }

    private sealed record CurrentFlowDotVisual(
        Ellipse Element,
        IReadOnlyList<(Point Start, Point End, double Length)> Segments,
        double TotalLength,
        double BaseDistance,
        bool Forward);

    private sealed record BlinkingLampVisual(
        Guid ComponentId,
        Ellipse Halo,
        Ellipse Bulb,
        Line[] Lines,
        Polyline SignalLine,
        IBrush OffStroke,
        IBrush OnStroke);

    private static string FriendlyName(ComponentKind kind) => kind switch
    {   ComponentKind.LimitSwitchNO => "Концевик NO",
        ComponentKind.LimitSwitchNC => "Концевик NC",
        ComponentKind.PushButtonNO => "Кнопка NO",
        ComponentKind.PushButtonNC => "Кнопка NC",
        ComponentKind.PushButtonNO_NC => "Кнопка NO+NC",
        ComponentKind.PushButtonCon => "Кнопка CON",
        ComponentKind.ToggleSwitchNO => "Переключатель NO",
        ComponentKind.ToggleSwitchNC => "Переключатель NC",
        ComponentKind.ToggleSwitchNO_NC => "Переключатель NO+NC",
        ComponentKind.ToggleSwitchCon => "Переключатель CON",
        ComponentKind.CrossoverSwitch => "Crossover switch",
        ComponentKind.OverloadNO => "Термореле NO (97-98)",
        ComponentKind.ThermalRelayAuxNC => "Термореле NC (95-96)",
        ComponentKind.ThermalRelayAuxNO_NC => "Термореле NC+NO (95-96/97-98)",
        ComponentKind.AuxContactNO => "Контакт NO",
        ComponentKind.AuxContactNC => "Контакт NC",
        ComponentKind.AuxContactNO_NC => "Контакт NO+NC",
        ComponentKind.ConContact => "CON contact",
        ComponentKind.OverloadNC => "Тепловая защита",
        ComponentKind.Coil => "Катушка",
        ComponentKind.Supply24V => "24V DC",
        ComponentKind.AcSupply => "Источник AC",
        ComponentKind.Lamp => "Лампа",
        ComponentKind.BlinkingLamp => "Лампа мигающая",
        ComponentKind.Motor => "Двигатель",
        ComponentKind.DcMotor => "Двигатель DC",
        ComponentKind.AcMotor => "Двигатель AC 3~",
        ComponentKind.AcMotor1P => "Двигатель AC 1~",
        ComponentKind.CircuitBreaker => "Автомат",
        ComponentKind.DifferentialBreaker => "Дифференциальный выключатель",
        ComponentKind.PowerPhase => "Фаза",
        ComponentKind.PowerNeutral => "Нейтраль",
        ComponentKind.PowerEarth => "Земля",
        ComponentKind.PowerPhaseNeutral => "Фаза и нейтраль",
        ComponentKind.PowerPhaseNeutralEarth => "Фаза, нейтраль и земля",
        ComponentKind.PowerThreePhase => "Три фазы",
        ComponentKind.PowerThreePhaseEarth => "Три фазы и земля",
        ComponentKind.PowerThreePhaseNeutral => "Три фазы и нейтраль",
        ComponentKind.PowerThreePhaseNeutralEarth => "Три фазы, нейтраль и земля",
        ComponentKind.PowerPlus => "Плюс DC",
        ComponentKind.PowerMinus => "Минус DC",
        ComponentKind.PowerDcEarth => "Земля DC",
        _ => kind.ToString()
    };

    private static double Snap(double value) => Math.Round(value / GridSize) * GridSize;
    private string NextTag(string prefix)
    {
        var used = _project.Components.Select(c => c.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = prefix.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (!used.Contains(stem)) return stem;
        for (int i = 1; i < 1000; i++) if (!used.Contains(stem + i)) return stem + i;
        return prefix + Guid.NewGuid().ToString("N")[..4];
    }

    private string GetPlacementTag(ComponentKind kind, string defaultTag)
    {
        if (kind == ComponentKind.Coil)
        {
            // Coils use the K, K1, K2... sequence.
            return NextRelayTag();
        }

        if (kind is ComponentKind.OverloadNO or ComponentKind.ThermalRelayAuxNC or ComponentKind.ThermalRelayAuxNO_NC)
        {
            var relay = _project.Components.LastOrDefault(c => c.Kind == ComponentKind.ThermalRelay);
            if (relay is not null) return relay.Tag;
        }

        return NextTag(defaultTag);
    }

    private string NextRelayTag()
    {
        var usedCoilTags = _project.Components
            .Where(c => c.Kind == ComponentKind.Coil)
            .Select(c => c.Tag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!usedCoilTags.Contains("K")) return "K";
        for (var i = 1; i < 1000; i++)
        {
            var candidate = "K" + i;
            if (!usedCoilTags.Contains(candidate)) return candidate;
        }
        return "K" + Guid.NewGuid().ToString("N")[..4];
    }

    private void RefreshSelectedStatus()
    {
        if (_selected is null) return;
        var label = FriendlyName(_selected.Kind);
        if (_simulator.IsRunning) label += " · " + _simulator.GetState(_selected.Id);
        StatusText.Text = $"Выбран {_selected.Tag}: {label}";
    }

   
private void UpdateSimulationButtons()
{
    RunButton.IsVisible = !_simulator.IsRunning;
    StopButton.IsVisible = _simulator.IsRunning;

    RunButton.IsEnabled = !_simulator.IsRunning;
    StopButton.IsEnabled = _simulator.IsRunning;

    StatusText.Text = _simulator.IsRunning
        ? _simulator.Message
        : "Симуляция остановлена";
}


    private void Run_Click(object sender, RoutedEventArgs e)
    {
        _placingKind = null; _wireMode = false; _wireStart = null;
        _simulator.Start(_project);
        _blinkOn = true;
        _currentDotOffset = 0;
        _currentFlowTickCount = 0;
        _blinkTimer.Start();
        _currentFlowTimer.Start();
        UpdateSimulationButtons(); Redraw();
        StatusText.Text = _simulator.Message;
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_pressedButtonId is Guid id) _simulator.SetButtonsByTag(id, false, _project);
        _pressedButtonId = null; ReleaseCapture();
        _simulator.Stop(); UpdateSimulationButtons(); Redraw();
        _blinkTimer.Stop(); _blinkOn = true;
        _currentFlowTimer.Stop(); _currentDotOffset = 0;
        _currentFlowTickCount = 0;
        StatusText.Text = "Симуляция остановлена. Редактирование схемы снова доступно.";
    }

    private void New_Click(object? sender, RoutedEventArgs e)
    {
        if (_simulator.IsRunning) _simulator.Stop();
        NewProject();
    }

    private void FileMenu_Click(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var create = new MenuItem { Header = L("Menu_New") };
        create.Click += New_Click;
        menu.Items.Add(create);
        var open = new MenuItem { Header = L("Menu_Open") };
        open.Click += Open_Click;
        menu.Items.Add(open);
        var save = new MenuItem { Header = L("Menu_Save") };
        save.Click += Save_Click;
        menu.Items.Add(save);
        menu.Open((Control)sender!);
    }

    private void SetTheme(bool dark)
    {
        _darkTheme = dark;
        if (Application.Current is { } app) app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        ThemeIconText.Text = dark ? "☼" : "☾";
        ThemeIconText.Foreground = dark ? Brush("#FBBF24") : Brush("#64748B");
        ThemeToggleButton.Background = dark ? Brush("#111827") : Brushes.White;
        ThemeToggleButton.BorderBrush = dark ? Brush("#334155") : Brush("#DCE4EF");
        TopBar.Background = dark ? Brush("#171717") : Brushes.White;
        TopBar.BorderBrush = dark ? Brush("#404040") : Brush("#D9E1EC");
        FileMenuButton.Foreground = dark ? Brushes.White : Brush("#172033");
        LanguageButton.Background = dark ? Brush("#1E293B") : Brushes.White;
        LanguageButton.Foreground = dark ? Brushes.White : Brush("#334155");
        LanguageButton.BorderBrush = dark ? Brush("#334155") : Brush("#DCE4EF");
        ToolboxBorder.Background = dark ? Brush("#202020") : Brushes.White;
        ToolboxBorder.BorderBrush = dark ? Brush("#404040") : Brush("#D9E1EC");
        foreach (var button in CategoryPanel.Children.OfType<Button>())
        {
            button.Background = dark ? Brush("#D1D5DB") : Brush("#F8FAFC");
            button.BorderBrush = dark ? Brush("#737373") : Brush("#CBD5E1");
        }
        SchematicCanvas.Background = dark ? Brushes.Black : Brushes.White;
        Viewport.Background = dark ? Brushes.Black : Brushes.White;
        _gridCount = -1;
        Redraw();
    }

    private void ThemeToggle_Click(object? sender, RoutedEventArgs e) => SetTheme(!_darkTheme);

    private static string L(string key) => LocalizationService.Get(key);

    private static string LocalizedPaletteTitle(string title)
    {
        var key = title switch
        {
            "Кнопка NO" => "Comp_PushButtonNO", "Кнопка NC" => "Comp_PushButtonNC",
            "Кнопка NO+NC" => "Comp_PushButtonNO_NC", "Кнопка CON" => "Comp_PushButtonCon",
            "Переключатель NO" => "Comp_ToggleSwitchNO", "Переключатель NC" => "Comp_ToggleSwitchNC",
            "Переключатель NO+NC" => "Comp_ToggleSwitchNO_NC", "Переключатель CON" => "Comp_ToggleSwitchCon",
            "Контакт NO" => "Comp_AuxContactNO", "Контакт NC" => "Comp_AuxContactNC",
            "Контакт NO+NC" => "Comp_AuxContactNO_NC", "CON contact" => "Comp_ConContact",
            "Концевик NO" => "Comp_LimitSwitchNO", "Концевик NC" => "Comp_LimitSwitchNC",
            "Термореле NO (97-98)" => "Comp_OverloadNO", "Термореле NC (95-96)" => "Comp_ThermalRelayAuxNC",
            "Термореле NC+NO (95-96/97-98)" => "Comp_ThermalRelayAuxNO_NC",
            "Контактор 2-полюсный" => "Comp_Contactor2P", "Контактор 3-полюсный" => "Comp_Contactor3P",
            "Контактор 4-полюсный" => "Comp_Contactor4P", "Катушка" => "Comp_Coil", "Тепловая защита" => "Comp_OverloadNC",
            "Автомат 1P" => "Comp_Breaker1P", "Автомат 1P+N" => "Comp_Breaker1PN", "Автомат 2P" => "Comp_Breaker2P",
            "Автомат 3P" => "Comp_Breaker3P", "Автомат 3P+N" => "Comp_Breaker3PN", "УЗО 1P+N" => "Comp_Rcd1PN",
            "УЗО 3P+N" => "Comp_Rcd3PN", "Тепловое реле" => "Comp_ThermalRelay",
            "Фаза" => "Comp_PowerPhase", "Нейтраль" => "Comp_PowerNeutral", "Земля" => "Comp_PowerEarth",
            "Фаза и нейтраль" => "Comp_PowerPhaseNeutral", "Фаза, нейтраль и земля" => "Comp_PowerPhaseNeutralEarth",
            "Три фазы" => "Comp_PowerThreePhase", "Три фазы и земля" => "Comp_PowerThreePhaseEarth",
            "Три фазы и нейтраль" => "Comp_PowerThreePhaseNeutral", "Три фазы, нейтраль и земля" => "Comp_PowerThreePhaseNeutralEarth",
            "Плюс DC" => "Comp_PowerPlus", "Минус DC" => "Comp_PowerMinus", "Земля DC" => "Comp_PowerDcEarth",
            "Лампа" => "Comp_Lamp", "Лампа мигающая" => "Comp_BlinkingLamp", "Двигатель AC 3~" => "Comp_AcMotor",
            "Двигатель AC 1~" => "Comp_AcMotor1P", "Двигатель DC" => "Comp_DcMotor",
            _ => ""
        };
        return string.IsNullOrEmpty(key) ? title : LocalizationService.Get(key);
    }

    private void ApplyLanguage()
    {
        FileMenuButton.Content = L("Menu_File");
        var languageIndex = (int)LocalizationService.CurrentLanguage;
        LanguageButton.Content = languageIndex switch
        {
            0 => "RU",
            1 => "EN",
            2 => "ES",
            _ => "ES",
        };
        
        var categoryKeys = new[] { "Cat_Power", "Cat_Breakers", "Cat_Contactors", "Cat_Motors", "Cat_Buttons", "Cat_Coils", "Cat_Relays" };
        var categories = CategoryPanel.Children.OfType<Button>().ToArray();
        for (var i = 0; i < Math.Min(categoryKeys.Length, categories.Length); i++)
            ToolTip.SetTip(categories[i], L(categoryKeys[i]));
        foreach (var button in PalettePanel.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Button>()))
            if (button.Tag is string title) ToolTip.SetTip(button, LocalizedPaletteTitle(title));
        RunButton.Content = $"▶ {L("Run")}";
        StopButton.Content = $"■ {L("Stop")}";
        // RotateButton.Content = L("Rotate");
        WorkAreaLabel.Text = L("Work_Area");
        StatusText.Text = _showingZoomStatus
        ? $"{L("Zoom_Status")} {Math.Round(_zoom * 100)}%."
        : L("Ready_Status");
    }

    private bool _showingZoomStatus = false;

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        await SaveProjectAsync();
    }

    private async Task SaveProjectAsync()
    {
        var path = _currentFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить проект",
                SuggestedFileName = _project.Name,
                DefaultExtension = "cforge",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("CircuitForge project (*.cforge)") { Patterns = new[] { "*.cforge" } },
                    new FilePickerFileType("JSON (*.json)") { Patterns = new[] { "*.json" } }
                }
            });
            path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            if (string.IsNullOrEmpty(System.IO.Path.GetExtension(path))) path += ".cforge";
        }
        _files.Save(path, _project);
        _currentFilePath = path;
        SaveCameraState();
        StatusText.Text = $"Сохранено: {System.IO.Path.GetFileName(path)}";
    }

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (_simulator.IsRunning) { StatusText.Text = "Останови симуляцию перед открытием другого проекта."; return; }
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Открыть проект",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("CircuitForge / CADe SIMU (*.cforge;*.json;*.cad)") { Patterns = new[] { "*.cforge", "*.json", "*.cad" } },
                FilePickerFileTypes.All
            }
        });
        var fileName = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(fileName)) return;
        try
        {
            SaveCameraState();
            var isCadSimu = IsCadSimuFile(fileName);
            string[] unsupportedCadParts = Array.Empty<string>();
            if (isCadSimu)
            {
                var imported = _cadImporter.Import(fileName);
                _project = imported.Project;
                unsupportedCadParts = imported.UnsupportedComponents.ToArray();
            }
            else _project = _files.Load(fileName);
            _currentFilePath = isCadSimu ? null : fileName;
            RestoreCameraState(_currentFilePath);
            _undoStack.Clear();
            foreach (var wire in _project.Wires)
                if (string.IsNullOrWhiteSpace(wire.ColorKey) || !WireColors.Any(c => c.Key == wire.ColorKey)) wire.ColorKey = "Brown";
            _selected = null; _wireMode = false; _wireStart = null; _placingKind = null;
            Redraw();

            string fileNameOnly = System.IO.Path.GetFileName(fileName);

            StatusText.Text = isCadSimu
                ? $"{LocalizationService.Get("ImportedFromCadSimu")}: {fileNameOnly}"
                : $"{LocalizationService.Get("Opened")}: {fileNameOnly}";
            if (unsupportedCadParts.Length > 0)
            {
                var listed = string.Join(Environment.NewLine, unsupportedCadParts.Take(12));
                var suffix = unsupportedCadParts.Length > 12 ? $"{Environment.NewLine}и ещё {unsupportedCadParts.Length - 12}" : "";
                await ShowMessageAsync("Неподдерживаемые элементы",
                    $"Некоторые элементы CADe SIMU пока не поддерживаются и не импортированы:{Environment.NewLine}{listed}{suffix}");
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Ошибка открытия", $"Не удалось открыть проект: {ex.Message}");
        }
    }

    private static bool IsCadSimuFile(string path)
    {
        using var reader = new System.IO.StreamReader(path);
        return reader.ReadLine()?.StartsWith("CADe_SIMU*", StringComparison.OrdinalIgnoreCase) == true;
    }
}

public readonly record struct TerminalRef(Guid ComponentId, string TerminalId);
public sealed record CameraState(double Zoom, double X, double Y);
public sealed record PaletteItem(string Title, ComponentKind? Kind, string DefaultTag, bool IsWire = false);
