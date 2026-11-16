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
    private readonly DispatcherTimer _timerSimTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
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
    private string? _activeCategory;
    private string? _activeSubcategory;

    private sealed record SubcategoryDef(string Key, string Category, string LocKey, ComponentKind IconKind);

    private static readonly SubcategoryDef[] Subcategories =
    {
        // 1. Электропитание
        new("Power_1P", "Электропитание", "Sub_Power_1P", ComponentKind.PowerPhase),
        new("Power_3P", "Электропитание", "Sub_Power_3P", ComponentKind.PowerThreePhase),
        new("Power_DC", "Электропитание", "Sub_Power_DC", ComponentKind.PowerPlus),

        // 2. Автоматы
        new("Breakers_Fuses", "Автоматы", "Sub_Breakers_Fuses", ComponentKind.Fuse),
        new("Breakers_Mcb",   "Автоматы", "Sub_Breakers_Mcb",   ComponentKind.Breaker1P),
        new("Breakers_Rcd",   "Автоматы", "Sub_Breakers_Rcd",   ComponentKind.Rcd1PN),

        // 3. Контакторы и переключатели
        new("Contacts_Aux",       "Контакторы и переключатели", "Sub_Contacts_Aux",       ComponentKind.AuxContactNC),
        new("Contacts_Timer",     "Контакторы и переключатели", "Sub_Contacts_Timer",     ComponentKind.TimerOnContactNO),
        new("Contacts_Thermal",   "Контакторы и переключатели", "Sub_Contacts_Thermal",   ComponentKind.OverloadNO),
        new("Contacts_Contactor", "Контакторы и переключатели", "Sub_Contacts_Contactor", ComponentKind.Contactor2P),

        // 4. Моторы
        new("Motors_AC", "Моторы", "Sub_Motors_AC", ComponentKind.AcMotor),
        new("Motors_DC", "Моторы", "Sub_Motors_DC", ComponentKind.DcMotor),

        // 5. Кнопки
        new("Buttons_Push",   "Кнопки", "Sub_Buttons_Push",   ComponentKind.PushButtonNO),
        new("Buttons_Switch", "Кнопки", "Sub_Buttons_Switch", ComponentKind.ToggleSwitchNO),
        new("Buttons_Limit",  "Кнопки", "Sub_Buttons_Limit",  ComponentKind.LimitSwitchNO),

        // 6. Катушки
        new("Coils_Coils",  "Катушки", "Sub_Coils_Coils",  ComponentKind.Coil),
        new("Coils_Timers", "Катушки", "Sub_Coils_Timers", ComponentKind.TimerOnDelay),
        new("Coils_Lamps",  "Катушки", "Sub_Coils_Lamps",  ComponentKind.Lamp),
        new("Coils_Sound",  "Катушки", "Sub_Coils_Sound",  ComponentKind.Bell),
    };
    private  Stack<ProjectDocument> _undoStack = new();
    private sealed class ProjectTab
    {   
        public bool IsDirty { get; set; }
        public Guid Id { get; } = Guid.NewGuid();
        public ProjectDocument Project { get; set; } = new();
        public string? FilePath { get; set; }
        public string DisplayTitle => string.IsNullOrWhiteSpace(FilePath)
            ? (string.IsNullOrWhiteSpace(Project.Name) ? "Untitled" : Project.Name)
            : System.IO.Path.GetFileNameWithoutExtension(FilePath);
        public Stack<ProjectDocument> UndoStack { get; } = new();
        public double Zoom { get; set; } = 1.0;
        public double PanX { get; set; }
        public double PanY { get; set; }
        public bool CameraInitialized { get; set; }
    }

    private readonly List<ProjectTab> _tabs = new();
    private ProjectTab? _activeTab;
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
    private bool _dragOverCanvas;
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
    private List<ComponentModel> _clipboardComponents = new();
    private bool _isPlacingPaste;
    private Point _clipboardCenter;
    private bool _isClosingWindow;
    private bool _forceClose;
    private List<WireModel> _clipboardWires = new();
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


    private static readonly (string Key, string LocKey, string Hex)[] WireColors =
    {
        ("Brown", "Wire_Color_Brown", "#8B4513"),
        ("Black", "Wire_Color_Black", "#202124"),
        ("Grey", "Wire_Color_Grey", "#6B7280"),
        ("Blue", "Wire_Color_Blue", "#2563EB"),
        ("GreenYellow", "Wire_Color_GreenYellow", "#65A30D"),
        ("Red", "Wire_Color_Red", "#DC2626")
    };

    private static readonly (string LocKey, string Hex)[] LampGlowColors =
    {
        ("Glow_Yellow", "#FDE047"), ("Glow_Orange", "#FB923C"),
        ("Glow_Red", "#F87171"), ("Glow_Green", "#4ADE80")
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
        Opened += (_, _) =>
        {
            FitToWorkArea();
            if (string.IsNullOrWhiteSpace(_currentFilePath))
            {
                RestoreCameraState(null);
                if (_activeTab is not null)
                {
                    _activeTab.Zoom = _zoom;
                    _activeTab.PanX = _panTransform.X;
                    _activeTab.PanY = _panTransform.Y;
                    _activeTab.CameraInitialized = true;
                }
            }
        };
        Closing += async (sender, e) =>
        {
            if (_forceClose) return;

            var dirtyTabs = _tabs.Where(t => t.IsDirty).ToList();
            if (dirtyTabs.Count > 0)
            {
                e.Cancel = true; // Приостанавливаем закрытие, чтобы показать диалог
                foreach (var tab in dirtyTabs)
                {
                    var res = await PromptSaveConfirmationAsync(tab);
                    if (res == SaveConfirmResult.Cancel)
                        return; // Пользователь нажал "Отмена" — остаёмся в программе
                    
                    // Если нажали "Не сохранять" или файл успешно сохранился — сбрасываем флаг изменений
                    tab.IsDirty = false;
                }
            }

            _forceClose = true;
            SaveCameraState();
            Close(); // Теперь закроется мгновенно
        };
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
                StatusText.Text = L("Anim_Error", ex.Message);
            }
        };
        _timerSimTimer.Tick += (_, _) =>
        {
            if (!_simulator.IsRunning) return;
            if (_simulator.Step(_project))
            {
                if (_selected is not null) RefreshSelectedStatus();
                else StatusText.Text = _simulator.Message;
                Redraw();
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
        Viewport.AddHandler(DragDrop.DragLeaveEvent, Canvas_DragLeave);
        Viewport.AddHandler(InputElement.PointerWheelChangedEvent, Canvas_MouseWheel, RoutingStrategies.Tunnel);
        Viewport.AddHandler(InputElement.PointerPressedEvent, Canvas_MiddleMouseDown, RoutingStrategies.Tunnel);
        Viewport.AddHandler(InputElement.PointerMovedEvent, Canvas_MiddleMouseMove, RoutingStrategies.Tunnel);
        Viewport.AddHandler(InputElement.PointerReleasedEvent, Canvas_MiddleMouseUp, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, Window_PreviewKeyDown, RoutingStrategies.Tunnel);
        NewProject();
        ApplyLanguage();
        SetTheme(IsDarkTime(DateTime.Now));
    }

    private void CopySelected()
    {
        if (_selectedComponentIds.Count == 0 && _selected is not null)
            _selectedComponentIds.Add(_selected.Id);

        if (_selectedComponentIds.Count == 0) return;

        var selectedSet = _selectedComponentIds.ToHashSet();

        _clipboardComponents = _project.Components
            .Where(c => selectedSet.Contains(c.Id))
            .Select(c => new ComponentModel
            {
                Id = c.Id,
                Kind = c.Kind,
                Tag = c.Tag,
                X = c.X,
                Y = c.Y,
                Rotation = c.Rotation,
                Phases = c.Phases,
                Voltage = c.Voltage,
                DelayOnSeconds = c.DelayOnSeconds,
                DelayOffSeconds = c.DelayOffSeconds,
                EnableTon = c.EnableTon,
                EnableTof = c.EnableTof,
                GlowColorHex = c.GlowColorHex
            }).ToList();

        _clipboardWires = _project.Wires
            .Where(w => selectedSet.Contains(w.StartComponentId) && selectedSet.Contains(w.EndComponentId))
            .Select(w => new WireModel
            {
                Id = w.Id,
                StartComponentId = w.StartComponentId,
                StartTerminalId = w.StartTerminalId,
                EndComponentId = w.EndComponentId,
                EndTerminalId = w.EndTerminalId,
                ColorKey = w.ColorKey,
                Waypoints = (w.Waypoints ?? new List<WireWaypoint>())
                    .Select(p => new WireWaypoint { X = p.X, Y = p.Y }).ToList()
            }).ToList();

        // Вычисляем геометрический центр копируемой группы, чтобы цеплять её к курсору
        var avgX = _clipboardComponents.Average(c => c.X);
        var avgY = _clipboardComponents.Average(c => c.Y);
        _clipboardCenter = new Point(Snap(avgX), Snap(avgY));

        StatusText.Text = L("Clipboard_Copied", _clipboardComponents.Count);
    }

    private void CutSelected()
    {
        if (_simulator.IsRunning) return;
        CopySelected();
        if (_clipboardComponents.Count > 0)
        {
            var count = _clipboardComponents.Count;
            DeleteSelected();
            StatusText.Text = L("Clipboard_Cut", count);
        }
    }

    private void StartPastePlacement()
    {
        if (_simulator.IsRunning || _clipboardComponents.Count == 0) return;

        _selected = null;
        _selectedComponentIds.Clear();
        _selectedWireId = null;
        _placingKind = null;
        _wireMode = false;
        _isPlacingPaste = true;

        StatusText.Text = L("Clipboard_Placing");
        Redraw();
    }

    private void CommitPastedPlacement(Point clickPos)
    {
        if (!_isPlacingPaste || _clipboardComponents.Count == 0) return;

        PushUndoState();

        var targetCenter = new Point(Snap(clickPos.X), Snap(clickPos.Y));
        var deltaX = targetCenter.X - _clipboardCenter.X;
        var deltaY = targetCenter.Y - _clipboardCenter.Y;

        var idMap = new Dictionary<Guid, Guid>();
        var newSelectedIds = new HashSet<Guid>();
        ComponentModel? lastPasted = null;

        foreach (var c in _clipboardComponents)
        {
            var newId = Guid.NewGuid();
            idMap[c.Id] = newId;

            var newComp = new ComponentModel
            {
                Id = newId,
                Kind = c.Kind,
                Tag = NextTag(c.Tag),
                X = Snap(c.X + deltaX),
                Y = Snap(c.Y + deltaY),
                Rotation = c.Rotation,
                Phases = c.Phases,
                Voltage = c.Voltage,
                DelayOnSeconds = c.DelayOnSeconds,
                DelayOffSeconds = c.DelayOffSeconds,
                EnableTon = c.EnableTon,
                EnableTof = c.EnableTof,
                GlowColorHex = c.GlowColorHex
            };

            _project.Components.Add(newComp);
            newSelectedIds.Add(newId);
            lastPasted = newComp;
        }

        foreach (var w in _clipboardWires)
        {
            if (idMap.TryGetValue(w.StartComponentId, out var newStartId) &&
                idMap.TryGetValue(w.EndComponentId, out var newEndId))
            {
                var newWaypoints = (w.Waypoints ?? new List<WireWaypoint>())
                    .Select(p => new WireWaypoint { X = Snap(p.X + deltaX), Y = Snap(p.Y + deltaY) }).ToList();

                _project.Wires.Add(new WireModel
                {
                    StartComponentId = newStartId,
                    StartTerminalId = w.StartTerminalId,
                    EndComponentId = newEndId,
                    EndTerminalId = w.EndTerminalId,
                    ColorKey = w.ColorKey,
                    Waypoints = newWaypoints
                });
            }
        }

        _isPlacingPaste = false;
        _selectedComponentIds.Clear();
        foreach (var id in newSelectedIds) _selectedComponentIds.Add(id);
        _selected = lastPasted;

        RefreshSelectedStatus();
        Redraw();
        StatusText.Text = L("Clipboard_Pasted", newSelectedIds.Count);
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
        StatusText.Text = L("Fit_Status");
    }

    private string CameraStatePath => System.IO.Path.Combine(AppContext.BaseDirectory, "camera-state.json");

    private enum SaveConfirmResult { Save, DontSave, Cancel }

    private async Task<SaveConfirmResult> PromptSaveConfirmationAsync(ProjectTab tab)
    {
        var tcs = new TaskCompletionSource<SaveConfirmResult>();

        var saveBtn = new Button { Content = L("Btn_Save"), Width = 95, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var dontSaveBtn = new Button { Content = L("Btn_DontSave"), Width = 110, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancelBtn = new Button { Content = L("Cancel_Action"), Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { saveBtn, dontSaveBtn, cancelBtn }
        };

        var dialog = new Window
        {
            Title = L("Dialog_Save_Title"),
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = string.Format(L("Dialog_Save_Message"), tab.DisplayTitle),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13
                    },
                    btnPanel
                }
            }
        };

        saveBtn.Click += async (_, _) =>
        {
            var prevTab = _activeTab;
            if (_activeTab != tab) SwitchToTab(tab);
            var saved = await SaveProjectAsync();
            if (_activeTab != prevTab && prevTab is not null) SwitchToTab(prevTab);

            tcs.TrySetResult(saved ? SaveConfirmResult.Save : SaveConfirmResult.Cancel);
            dialog.Close();
        };

        dontSaveBtn.Click += (_, _) =>
        {
            tcs.TrySetResult(SaveConfirmResult.DontSave);
            dialog.Close();
        };

        cancelBtn.Click += (_, _) =>
        {
            tcs.TrySetResult(SaveConfirmResult.Cancel);
            dialog.Close();
        };

        dialog.Closed += (_, _) => tcs.TrySetResult(SaveConfirmResult.Cancel);

        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

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

    private void BuildPalette()
    {
        // 1. Электропитание -> Однофазное
        AddPalette("Фаза", ComponentKind.PowerPhase, "X1");
        AddPalette("Нейтраль", ComponentKind.PowerNeutral, "X1");
        AddPalette("Земля", ComponentKind.PowerEarth, "X1");
        AddPalette("Фаза и нейтраль", ComponentKind.PowerPhaseNeutral, "X1");
        AddPalette("Фаза, нейтраль и земля", ComponentKind.PowerPhaseNeutralEarth, "X1");

        // 1. Электропитание -> Трёхфазное
        AddPalette("Три фазы", ComponentKind.PowerThreePhase, "X1");
        AddPalette("Три фазы и земля", ComponentKind.PowerThreePhaseEarth, "X1");
        AddPalette("Три фазы и нейтраль", ComponentKind.PowerThreePhaseNeutral, "X1");
        AddPalette("Три фазы, нейтраль и земля", ComponentKind.PowerThreePhaseNeutralEarth, "X1");

        // 1. Электропитание -> Постоянное
        AddPalette("Плюс DC", ComponentKind.PowerPlus, "XDC1");
        AddPalette("Минус DC", ComponentKind.PowerMinus, "XDC1");
        AddPalette("Земля DC", ComponentKind.PowerDcEarth, "XDC1");

        // 2. Автоматы -> Предохранители
        AddPalette("Предохранитель", ComponentKind.Fuse, "FU1");

        // 2. Автоматы -> Автоматы (включая термореле)
        AddPalette("Автомат 1P", ComponentKind.Breaker1P, "QF1");
        AddPalette("Автомат 1P+N", ComponentKind.Breaker1PN, "QF2");
        AddPalette("Автомат 2P", ComponentKind.Breaker2P, "QF3");
        AddPalette("Автомат 3P", ComponentKind.Breaker3P, "QF4");
        AddPalette("Автомат 3P+N", ComponentKind.Breaker3PN, "QF5");
        AddPalette("Тепловое реле", ComponentKind.ThermalRelay, "F2");

        // 2. Автоматы -> Дифф автоматы (УЗО)
        AddPalette("УЗО 1P+N", ComponentKind.Rcd1PN, "QFD2");
        AddPalette("УЗО 3P+N", ComponentKind.Rcd3PN, "QFD3");

        // 3. Контакторы и переключатели -> Вспомогательные контакты (NC, NO, NO+NC, CON)
        AddPalette("Контакт NC", ComponentKind.AuxContactNC, "K1");
        AddPalette("Контакт NO", ComponentKind.AuxContactNO, "K1");
        AddPalette("Контакт NO+NC", ComponentKind.AuxContactNO_NC, "K1");
        AddPalette("CON contact", ComponentKind.ConContact, "K1");

        // 3. Контакторы и переключатели -> Контакты таймера
        AddPalette("Контакт таймера вкл. NO", ComponentKind.TimerOnContactNO, "KT1");
        AddPalette("Контакт таймера вкл. NC", ComponentKind.TimerOnContactNC, "KT1");
        AddPalette("Контакт таймера выкл. NO", ComponentKind.TimerOffContactNO, "KT1");
        AddPalette("Контакт таймера выкл. NC", ComponentKind.TimerOffContactNC, "KT1");
        AddPalette("Контакт таймера вкл/выкл", ComponentKind.TimerOnOffContactNO_NC, "KT1");

        // 3. Контакторы и переключатели -> Контакты термореле
        AddPalette("Термореле NO (97-98)", ComponentKind.OverloadNO, "F1");
        AddPalette("Термореле NC (95-96)", ComponentKind.ThermalRelayAuxNC, "F2");
        AddPalette("Термореле NC+NO (95-96/97-98)", ComponentKind.ThermalRelayAuxNO_NC, "F2");

        // 3. Контакторы и переключатели -> Контакторы
        AddPalette("Контактор 2-полюсный", ComponentKind.Contactor2P, "KM");
        AddPalette("Контактор 3-полюсный", ComponentKind.Contactor3P, "KM");
        AddPalette("Контактор 4-полюсный", ComponentKind.Contactor4P, "KM");

        // 4. Моторы -> Двигатели AC
        AddPalette("Двигатель AC 3~", ComponentKind.AcMotor, "M2");
        AddPalette("Двигатель AC 1~", ComponentKind.AcMotor1P, "M3");

        // 4. Моторы -> Двигатели DC
        AddPalette("Двигатель DC", ComponentKind.DcMotor, "M1");

        // 5. Кнопки -> Кнопки
        AddPalette("Кнопка NO", ComponentKind.PushButtonNO, "S1");
        AddPalette("Кнопка NC", ComponentKind.PushButtonNC, "S0");
        AddPalette("Кнопка NO+NC", ComponentKind.PushButtonNO_NC, "S1");
        AddPalette("Кнопка CON", ComponentKind.PushButtonCon, "S1");

        // 5. Кнопки -> Переключатели
        AddPalette("Переключатель NO", ComponentKind.ToggleSwitchNO, "SA1");
        AddPalette("Переключатель NC", ComponentKind.ToggleSwitchNC, "SA1");
        AddPalette("Переключатель NO+NC", ComponentKind.ToggleSwitchNO_NC, "SA1");
        AddPalette("Переключатель CON", ComponentKind.ToggleSwitchCon, "SA1");
        AddPalette("Crossover switch", ComponentKind.CrossoverSwitch, "SA1");

        // 5. Кнопки -> Концевики
        AddPalette("Концевик NO", ComponentKind.LimitSwitchNO, "FC1");
        AddPalette("Концевик NC", ComponentKind.LimitSwitchNC, "FC1");

        // 6. Катушки -> Катушки
        AddPalette("Катушка", ComponentKind.Coil, "K1");

        // 6. Катушки -> Таймеры
        AddPalette("Таймер задержки включения", ComponentKind.TimerOnDelay, "KT1");
        AddPalette("Таймер задержки выключения", ComponentKind.TimerOffDelay, "KT1");
        AddPalette("Таймер вкл/выкл", ComponentKind.TimerOnOffDelay, "KT1");

        // 6. Катушки -> Лампочки
        AddPalette("Лампа", ComponentKind.Lamp, "H1");
        AddPalette("Лампа мигающая", ComponentKind.BlinkingLamp, "H2");

        // 6. Катушки -> Звук
        AddPalette("Звонок", ComponentKind.Bell, "HA1");
        AddPalette("Сирена", ComponentKind.Siren, "HA2");
    }

    private static string SubcategoryFor(string title) => title switch
    {
        // 1. Электропитание
        "Фаза" or "Нейтраль" or "Земля" or "Фаза и нейтраль" or "Фаза, нейтраль и земля" => "Power_1P",
        "Три фазы" or "Три фазы и земля" or "Три фазы и нейтраль" or "Три фазы, нейтраль и земля" => "Power_3P",
        "Плюс DC" or "Минус DC" or "Земля DC" => "Power_DC",

        // 2. Автоматы
        "Предохранитель" => "Breakers_Fuses",
        "Автомат 1P" or "Автомат 1P+N" or "Автомат 2P" or "Автомат 3P" or "Автомат 3P+N" or "Тепловое реле" => "Breakers_Mcb",
        "УЗО 1P+N" or "УЗО 3P+N" => "Breakers_Rcd",

        // 3. Контакторы и переключатели
        "Контакт NC" or "Контакт NO" or "Контакт NO+NC" or "CON contact" => "Contacts_Aux",
        "Контакт таймера вкл. NO" or "Контакт таймера вкл. NC" or "Контакт таймера выкл. NO" or "Контакт таймера выкл. NC" or "Контакт таймера вкл/выкл" => "Contacts_Timer",
        "Термореле NO (97-98)" or "Термореле NC (95-96)" or "Термореле NC+NO (95-96/97-98)" => "Contacts_Thermal",
        "Контактор 2-полюсный" or "Контактор 3-полюсный" or "Контактор 4-полюсный" => "Contacts_Contactor",

        // 4. Моторы
        "Двигатель AC 3~" or "Двигатель AC 1~" => "Motors_AC",
        "Двигатель DC" => "Motors_DC",

        // 5. Кнопки
        "Кнопка NO" or "Кнопка NC" or "Кнопка NO+NC" or "Кнопка CON" => "Buttons_Push",
        "Переключатель NO" or "Переключатель NC" or "Переключатель NO+NC" or "Переключатель CON" or "Crossover switch" => "Buttons_Switch",
        "Концевик NO" or "Концевик NC" => "Buttons_Limit",

        // 6. Катушки
        "Катушка" => "Coils_Coils",
        "Таймер задержки включения" or "Таймер задержки выключения" or "Таймер вкл/выкл" => "Coils_Timers",
        "Лампа" or "Лампа мигающая" => "Coils_Lamps",
        "Звонок" or "Сирена" => "Coils_Sound",

        _ => "Other"
    };

private void FilterPalette(string category)
    {
        _activeCategory = category;
        _activeSubcategory = null;
        ToolboxBorder.VerticalAlignment = VerticalAlignment.Top;
        UpdateCategoryButtonsAppearance();

        SubcategoryPanel.Children.Clear();
        var subs = Subcategories.Where(s => s.Category == category).ToArray();
        SubcategoryRow.IsVisible = subs.Length > 0;
        PaletteRow.IsVisible = false;

        for (var i = 0; i < subs.Length; i++)
        {
            var sub = subs[i];
            var icon = MiniSymbol(sub.IconKind, false);
            if (icon is Panel iconPanel && category is "Контакторы и переключатели" or "Кнопки")
                SetSymbolStrokeThicknessForDisplay(iconPanel, 1.4);

            var btnBg = _darkTheme ? Brush("#2A2F3A") : Brushes.White;
            var iconFg = _darkTheme ? Brush("#E2E8F0") : Brushes.Black;
            ApplyIconForeground(icon, iconFg, btnBg);

            var subBtn = new Button
            {
                Padding = new Thickness(6, 5, 6, 5),
                Margin = new Thickness(2, 0, 2, 0),
                Foreground = iconFg,
                Tag = sub.Key,
                Content = icon
            };
            subBtn.Classes.Add("pal");
            ToolTip.SetTip(subBtn, L(sub.LocKey));
            ToolTip.SetShowDelay(subBtn, 350);

            var capturedKey = sub.Key;
            subBtn.Click += (_, _) => SelectSubcategory(capturedKey);
            SubcategoryPanel.Children.Add(subBtn);

            if (i < subs.Length - 1)
            {
                SubcategoryPanel.Children.Add(new Border
                {
                    Width = 1,
                    Height = 28,
                    Background = Brush("#CBD5E1"),
                    Margin = new Thickness(4, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                });
            }
        }

        var catKey = category switch
        {
            "Электропитание" => "Cat_Power",
            "Автоматы" => "Cat_Breakers",
            "Контакторы и переключатели" => "Cat_Contactors",
            "Моторы" => "Cat_Motors",
            "Кнопки" => "Cat_Buttons",
            "Катушки" => "Cat_Coils",
            "Реле" => "Cat_Relays",
            _ => "Cat_Other"
        };
        StatusText.Text = L("Category_Status", L(catKey));
    }

    private void SelectSubcategory(string subcategoryKey)
    {
        _activeSubcategory = subcategoryKey;
        SubcategoryRow.IsVisible = false;
        PaletteRow.IsVisible = true;

        foreach (var child in PalettePanel.Children.OfType<Control>())
        {
            if (child.Tag is string itemSubcategory)
                child.IsVisible = itemSubcategory == subcategoryKey;
        }

        var sub = Subcategories.FirstOrDefault(s => s.Key == subcategoryKey);
        if (sub is not null)
            StatusText.Text = L("Category_Status", L(sub.LocKey));
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

    private void AddPalette(string title, ComponentKind? kind, string tag, bool hasSubmenu = false)
    {
        // Keep the palette model in sync with the visible palette buttons.
        // Without this entry, placement calls _palette.First(...) and crashes.
        _palette.Add(new PaletteItem(title, kind, tag, IsWire: kind is null));

        var group = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2, 0, 4, 4),
            Tag = SubcategoryFor(title),
            IsVisible = false
        };
        var main = new Button
        {
            Padding = new Thickness(6, 5, 6, 5),
            Foreground = _darkTheme ? Brush("#E2E8F0") : Brush("#1E293B"),
            Tag = title
        };
        main.Classes.Add("pal");
        ToolTip.SetTip(main, LocalizedPaletteTitle(title));
        ToolTip.SetShowDelay(main, 350);
        var body = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var symbol = MiniSymbol(kind, title == "Провод");
        if (symbol is Panel symbolPanel && SubcategoryFor(title) is "Contacts_Aux" or "Contacts_Timer" or "Contacts_Thermal" or "Contacts_Contactor" or "Buttons_Push" or "Buttons_Switch" or "Buttons_Limit")
            SetSymbolStrokeThicknessForDisplay(symbolPanel, 1.4);
        body.Children.Add(symbol);
        main.Content = body;
        ApplyIconForeground(symbol, _darkTheme ? Brush("#E2E8F0") : Brush("#010101"), _darkTheme ? Brush("#2A2F3A") : Brushes.White);
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
               finally
                {
                    _dragInFlight = null;
                    _dragOverCanvas = false;
                    Redraw();
                }
            }, RoutingStrategies.Tunnel);
        }
        main.Click += (_, _) =>
        {
            if (_simulator.IsRunning)
            {
                StatusText.Text = L("Sim_Stop_First");
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
                ? L("Wire_Mode_Status", CurrentWireColorLabel())
                : L("Preview_Status", LocalizedPaletteTitle(title));
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
            ToolTip.SetTip(arrow, title == "Провод" ? L("Wire_Color") : L("Ac_Source_Tip"));
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
        var selectedWireIdAtOpen = _selectedWireId;
        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        foreach (var color in WireColors)
        {
            var colorName = L(color.LocKey);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border
            {
                Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0),
                Background = Brush(color.Hex), BorderBrush = Brush("#CBD5E1"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3)
            });
            row.Children.Add(new TextBlock { Text = colorName, VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Header = row, ToggleType = MenuItemToggleType.CheckBox, IsChecked = _selectedWireColorKey == color.Key };
            item.Click += (_, _) =>
            {
                _selectedWireColorKey = color.Key;
                WireColorDot.Fill = Brush(WireColorHex(_selectedWireColorKey));
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
                    StatusText.Text = L("Wire_Selected_Color_Changed", colorName);
                }
                else
                {
                    StatusText.Text = L("Wire_New_Color", colorName);
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
        menu.Items.Add(AcChoice(L("Ac_1P_230"), 1, 230));
        menu.Items.Add(AcChoice(L("Ac_1P_24"), 1, 24));
        menu.Items.Add(new Separator());
        menu.Items.Add(AcChoice(L("Ac_3P_400"), 3, 400));
        menu.Items.Add(AcChoice(L("Ac_3P_230"), 3, 230));
        return menu;
    }

    private MenuItem AcChoice(string label, int phases, int voltage)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) =>
        {
            if (_simulator.IsRunning)
            {
                StatusText.Text = L("Sim_Stop_First");
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
            StatusText.Text = L("Ac_Source_Status", phases, voltage);
            Redraw();
        };
        return item;
    }
    private static void AddMiniBreakerPole(Canvas canvas, double x, bool isNeutral, IBrush p)
    {
        var glyph = new Canvas
        {
            Width = 22,
            Height = 26,
            RenderTransform = ScaleAbout(0.86, 0.86, 11, 13),
            RenderTransformOrigin = RelativePoint.TopLeft
        };

        if (isNeutral)
        {
            // Полюс нейтрали (N) — ровный контакт без расцепителя
            AddLine(glyph, 13.0, -2, 13.0, 8, p, 1.4);
            AddLine(glyph, 5.0, 11.6, 13.0, 16.0, p, 1.4);
            AddLine(glyph, 13.0, 16.2, 13.0, 25.2, p, 1.4);
        }
        else
        {
            // Фазный полюс с тепловым/магнитным расцепителем
            AddLine(glyph, 13.5, -4, 13.5, 4, p, 1.4);
            AddLine(glyph, 11, 2, 16, 9, p, 1.4);
            AddLine(glyph, 16, 2, 11, 9, p, 1.4);
            AddLine(glyph, 11, 10, 16, 10, p, 1.4);
            AddLine(glyph, 5.0, 11.6, 13.0, 16.0, p, 1.4);
            AddLine(glyph, 13.0, 16.2, 13.0, 25.2, p, 1.4);
        }

        Canvas.SetLeft(glyph, x);
        canvas.Children.Add(glyph);
    }
    private Control MiniSymbol(ComponentKind? kind, bool wire)
    {
        var isThreePhaseNeutralEarth = kind == ComponentKind.PowerThreePhaseNeutralEarth;
        var c = new Canvas
        {
            Width = kind switch
            {
                ComponentKind.Breaker1P => 22,
                ComponentKind.Breaker1PN or ComponentKind.Breaker2P => 33,
                ComponentKind.Breaker3P => 44,
                ComponentKind.Breaker3PN => 55,
                _ => 32
            },
            Height = isThreePhaseNeutralEarth ? 32 : 26
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
                var eLabel = new TextBlock
                {
                    Text = "E",
                    Foreground = p,
                    FontSize = 8,
                    FontStyle = FontStyle.Italic,
                    Tag = "NoZoom"
                };
                Canvas.SetLeft(eLabel, 0); Canvas.SetTop(eLabel, 6); c.Children.Add(eLabel);
            }
        }
        else if (kind == ComponentKind.Bell)
        {
            AddLine(c, 13, 0, 13, 5, p, 1.5);
            AddLine(c, 13, 21, 13, 26, p, 1.5);
            AddLine(c, 13, 5, 13, 21, p, 1.5);
            c.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M 13,5 A 8,8 0 0,1 13,21"),
                Stroke = p, StrokeThickness = 1.5, Fill = Brushes.Transparent
            });
            AddLine(c, 9, 13, 13, 13, p, 1.3);
            c.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = p });
            Canvas.SetLeft(c.Children[^1], 6); Canvas.SetTop(c.Children[^1], 11);
        }
        else if (kind == ComponentKind.Siren)
        {
            AddLine(c, 11, 0, 11, 6, p, 1.5);
            AddLine(c, 11, 20, 11, 26, p, 1.5);
            c.Children.Add(new Rectangle { Width = 7, Height = 14, Stroke = p, StrokeThickness = 1.5, Fill = Brushes.Transparent });
            Canvas.SetLeft(c.Children[^1], 8); Canvas.SetTop(c.Children[^1], 6);
            c.Children.Add(new Polygon
            {
                Points = new PointCollection { new(15, 10), new(25, 5), new(25, 21), new(15, 16) },
                Stroke = p, StrokeThickness = 1.5, Fill = Brushes.Transparent
            });
        }
        else if (kind == ComponentKind.Fuse)
            {
                AddLine(c, 16, 1, 16, 25, p, 1.6);
                c.Children.Add(new Rectangle 
                { 
                    Width = 8, 
                    Height = 16, 
                    Stroke = p, 
                    StrokeThickness = 1.5, 
                    Fill = Brushes.Transparent 
                });
                Canvas.SetLeft(c.Children[^1], 12); 
                Canvas.SetTop(c.Children[^1], 5);
                SetSymbolStrokeThickness(c, 1.4);
                return c;
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
            var eLabel = new TextBlock
            {
                Text = "E",
                Foreground = p,
                FontSize = 13,
                FontStyle = FontStyle.Italic,
                Tag = "NoZoom"
            };
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
        else if (kind is ComponentKind.TimerOnContactNO or ComponentKind.TimerOnContactNC
            or ComponentKind.TimerOffContactNO or ComponentKind.TimerOffContactNC)
        {
            var isNc = kind is ComponentKind.TimerOnContactNC or ComponentKind.TimerOffContactNC;
            var isOff = kind is ComponentKind.TimerOffContactNO or ComponentKind.TimerOffContactNC;
            AddLine(c, 16, 1, 16, 8, p, 1.7);
            AddLine(c, 16, 16, 16, 25, p, 1.7);
            if (isNc)
            {
                AddLine(c, 16, 8, 19, 8, p, 1.7);
                AddLine(c, 19, 8, 16, 16, p, 1.7);
            }
            else
            {
                AddLine(c, 11, 8, 16, 16, p, 1.7);
            }
            if (!isOff)
            {
                AddLine(c, 7, 11, 13, 11, p, 1.2);
                AddLine(c, 7, 13, 13, 13, p, 1.2);
                c.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M 8,8 Q 4,12 8,16"), Stroke = p, StrokeThickness = 1.4, Fill = Brushes.Transparent });
            }
            else
            {
                AddLine(c, 18, 11, 24, 11, p, 1.2);
                AddLine(c, 18, 13, 24, 13, p, 1.2);
                c.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M 23,8 Q 27,12 23,16"), Stroke = p, StrokeThickness = 1.4, Fill = Brushes.Transparent });
            }
        }
        else if (kind == ComponentKind.TimerOnOffContactNO_NC)
        {
            AddLine(c, 12, 2, 12, 9, p, 1.5);
            AddLine(c, 12, 9, 15, 9, p, 1.5);
            AddLine(c, 15, 9, 12, 17, p, 1.5);
            AddLine(c, 12, 17, 12, 24, p, 1.5);
            AddLine(c, 24, 2, 24, 9, p, 1.5);
            AddLine(c, 20, 9, 24, 17, p, 1.5);
            AddLine(c, 24, 17, 24, 24, p, 1.5);
            AddLine(c, 6, 12, 12, 12, p, 1.2);
            AddLine(c, 6, 14, 12, 14, p, 1.2);
            c.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M 5,9 Q 8,13 5,17 M 8,9 Q 5,13 8,17"), Stroke = p, StrokeThickness = 1.3, Fill = Brushes.Transparent });
            c.Children.Add(new Line { StartPoint = new Point(14, 13), EndPoint = new Point(22, 13), Stroke = p, StrokeThickness = 1.1, StrokeDashArray = new DoubleCollection { 1.5, 1.5 } });
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
                    FontStyle = FontStyle.Italic,
                    Tag = "NoZoom"
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
       else if (kind is ComponentKind.TimerOnDelay or ComponentKind.TimerOffDelay or ComponentKind.TimerOnOffDelay)
        {
            AddLine(c, 16, 0, 16, 5, p, 1.5);
            c.Children.Add(new Rectangle { Width = 26, Height = 14, Stroke = p, StrokeThickness = 1.5 });
            Canvas.SetLeft(c.Children[^1], 3); Canvas.SetTop(c.Children[^1], 5);
            if (kind == ComponentKind.TimerOnDelay)
            {
                AddLine(c, 11, 5, 11, 19, p, 1.5);
                AddLine(c, 3, 5, 11, 19, p, 1.4);
                AddLine(c, 11, 5, 3, 19, p, 1.4);
            }
            else if (kind == ComponentKind.TimerOffDelay)
            {
                c.Children.Add(new Rectangle { Width = 8, Height = 14, Fill = p, Stroke = p, StrokeThickness = 1.4 });
                Canvas.SetLeft(c.Children[^1], 3); Canvas.SetTop(c.Children[^1], 5);
            }
            else
            {
                c.Children.Add(new Rectangle { Width = 4, Height = 14, Fill = p, Stroke = p, StrokeThickness = 1.4 });
                Canvas.SetLeft(c.Children[^1], 3); Canvas.SetTop(c.Children[^1], 5);
                AddLine(c, 14, 5, 14, 19, p, 1.5);
                AddLine(c, 7, 5, 14, 19, p, 1.4);
                AddLine(c, 14, 5, 7, 19, p, 1.4);
            }
            AddLine(c, 16, 19, 16, 25, p, 1.5);
            SetSymbolStrokeThickness(c, 1.4);
            return c;
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
            var poles = kind switch
            {
                ComponentKind.Breaker1P => new[] { false },
                ComponentKind.Breaker1PN => new[] { false, true },
                ComponentKind.Breaker2P => new[] { false, false },
                ComponentKind.Breaker3P => new[] { false, false, false },
                ComponentKind.Breaker3PN => new[] { false, false, false, true },
                _ => Array.Empty<bool>()
            };

            const double spacing = 11.0;
            for (var i = 0; i < poles.Length; i++)
            {
                AddMiniBreakerPole(c, i * spacing, poles[i], p);
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
    private string CurrentWireColorLabel()
    {
        var locKey = WireColors.FirstOrDefault(c => c.Key == _selectedWireColorKey).LocKey;
        return string.IsNullOrEmpty(locKey) ? _selectedWireColorKey : L(locKey);
    }
    private string WireColorHex(string key) => WireColors.FirstOrDefault(c => c.Key == key).Hex ?? "#8B4513";

    private void StopSimulationInternal()
    {
        if (!_simulator.IsRunning) return;
        if (_pressedButtonId is Guid id) _simulator.SetButtonsByTag(id, false, _project);
        _pressedButtonId = null;
        ReleaseCapture();
        _simulator.Stop();
        _blinkTimer.Stop();
        _blinkOn = true;
        _currentFlowTimer.Stop();
        _timerSimTimer.Stop();
        _currentDotOffset = 0;
        _currentFlowTickCount = 0;
        UpdateSimulationButtons();
    }

    private void SaveCurrentTabState()
    {
        if (_activeTab is null) return;
        _activeTab.Project = _project;
        _activeTab.FilePath = _currentFilePath;
        _activeTab.Zoom = _zoom;
        _activeTab.PanX = _panTransform.X;
        _activeTab.PanY = _panTransform.Y;
        _activeTab.CameraInitialized = true;
    }

    private void SwitchToTab(ProjectTab tab)
    {
        if (_activeTab == tab) return;
        StopSimulationInternal();
        SaveCurrentTabState();

        _selected = null; _selectedComponentIds.Clear(); _selectedWireId = null;
        _wireStart = null; _wireWaypoints.Clear(); _hoverTerminal = null;
        _placingKind = null; _wireMode = false; _dragging = false; _selectingArea = false;
        _dragOrigins.Clear(); _wireDragOrigins.Clear(); _pressedButtonId = null;
        ClearTerminalBatch();
        ReleaseCapture();

        _activeTab = tab;
        _project = tab.Project;
        _currentFilePath = tab.FilePath;
        _undoStack = tab.UndoStack;
        _dragUndoSaved = false;

        if (tab.CameraInitialized)
        {
            _zoom = tab.Zoom;
            _panTransform.X = tab.PanX;
            _panTransform.Y = tab.PanY;
            _zoomTransform.ScaleX = _zoom;
            _zoomTransform.ScaleY = _zoom;
        }
        else
        {
            RestoreCameraState(tab.FilePath);
            tab.Zoom = _zoom;
            tab.PanX = _panTransform.X;
            tab.PanY = _panTransform.Y;
            tab.CameraInitialized = true;
        }

        UpdateSimulationButtons();
        RebuildTabs();
        Redraw();
    }

    private async void CloseTab(ProjectTab tab)
    {
        if (tab.IsDirty)
        {
            var answer = await PromptSaveConfirmationAsync(tab);
            if (answer == SaveConfirmResult.Cancel) return;
        }

        var idx = _tabs.IndexOf(tab);
        if (idx < 0) return;

        if (_activeTab == tab)
        {
            StopSimulationInternal();
            SaveCameraState();
        }

        _tabs.RemoveAt(idx);

        if (_tabs.Count == 0)
        {
            _activeTab = null;
            NewProject();
            return;
        }

        if (_activeTab == tab)
        {
            _activeTab = null;
            var next = _tabs[Math.Clamp(idx, 0, _tabs.Count - 1)];
            SwitchToTab(next);
            return;
        }

        RebuildTabs();
    }
    private void RebuildTabs()
    {
        if (TabsPanel is null) return;
        TabsPanel.Children.Clear();

        // Проверяем условие: открыт только один файл и он еще без имени (Untitled)
        bool hideCloseOnSingle = false;
        if (_tabs.Count == 1)
        {
            var single = _tabs[0];
            bool isUntitled = string.IsNullOrWhiteSpace(single.FilePath) &&
                              (string.IsNullOrWhiteSpace(single.Project.Name) || single.Project.Name.Equals("Untitled", StringComparison.OrdinalIgnoreCase));
            if (isUntitled)
            {
                hideCloseOnSingle = true;
            }
        }

        foreach (var tab in _tabs)
        {
            var isActive = tab == _activeTab;
            var showCloseButton = !hideCloseOnSingle; // Во всех других случаях крестик отображается

            var bg = isActive
                ? (_darkTheme ? Brush("#1E293B") : Brush("#FFFFFF"))
                : (_darkTheme ? Brush("#262626") : Brush("#EDF2F7"));
            var border = isActive
                ? (_darkTheme ? Brush("#3B82F6") : Brush("#2563EB"))
                : (_darkTheme ? Brush("#404040") : Brush("#CBD5E1"));
            var fg = isActive
                ? (_darkTheme ? Brush("#F8FAFC") : Brush("#1E40AF"))
                : (_darkTheme ? Brush("#CBD5E1") : Brush("#64748B"));

            var card = new Border
            {
                Background = bg,
                BorderBrush = border,
                BorderThickness = new Thickness(1, 1, 1, isActive ? 2 : 1),
                CornerRadius = new CornerRadius(5, 5, 0, 0), // Закруглены только верхние углы
                Padding = new Thickness(9, 3, showCloseButton ? 4 : 9, 3),
                Height = 27,
                Cursor = new Cursor(StandardCursorType.Hand)
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 6
            };

            var titleText = new TextBlock
            {
                Text = tab.DisplayTitle + (tab.IsDirty ? " •" : ""),
                Foreground = fg,
                FontSize = 11.5,
                FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Normal,
                MaxWidth = 140,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(titleText, string.IsNullOrWhiteSpace(tab.FilePath) ? tab.DisplayTitle : tab.FilePath);
            row.Children.Add(titleText);

            if (showCloseButton)
            {
                var closeBtn = new Button
                {
                    Content = "×",
                    Width = 16,
                    Height = 16,
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = fg,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(3),
                    FontSize = 12,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Hand)
                };

                var capturedTab = tab;
                closeBtn.Click += (_, ev) =>
                {
                    ev.Handled = true;
                    CloseTab(capturedTab);
                };

                row.Children.Add(closeBtn);
            }

            var clickedTab = tab;
            card.PointerPressed += (_, ev) =>
            {
                if (!ev.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
                SwitchToTab(clickedTab);
                ev.Handled = true;
            };

            card.Child = row;
            TabsPanel.Children.Add(card);
        }
    }

    private void UndoToolbar_Click(object? sender, RoutedEventArgs e)
    {
        if (!_simulator.IsRunning) UndoLastAction();
        else StatusText.Text = L("Sim_Stop_Before_Undo");
    }

    private void RotateToolbar_Click(object? sender, RoutedEventArgs e)
    {
        if (!_simulator.IsRunning) RotateSelected();
        else StatusText.Text = L("Sim_Stop_Before_Rotate");
    }

    private void DeleteToolbar_Click(object? sender, RoutedEventArgs e)
    {
        if (!_simulator.IsRunning) DeleteSelected();
    }

    private void WireColorToolbar_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control target)
            BuildWireColorMenu(target).Open(target);
    }

    private void NewProject()
    {
        StopSimulationInternal();
        SaveCameraState();
        SaveCurrentTabState();

        var doc = new ProjectDocument { Name = "Untitled", SchemaVersion = 4 };
        var tab = new ProjectTab { Project = doc, FilePath = null };
        _tabs.Add(tab);

        _activeTab = null;
        SwitchToTab(tab);
        StatusText.Text = L("Ready_Status");
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
        ZoomStatusText.Text = $"{Math.Round(_zoom * 100)}%";
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

    private void Canvas_DragLeave(object? sender, DragEventArgs e)
    {
        if (!_dragOverCanvas) return;
        _dragOverCanvas = false;
        Redraw();
    }

    private void Canvas_DragOver(object? sender, DragEventArgs e)
    {
        var canDrop = !_simulator.IsRunning && _dragInFlight is not null;
        e.DragEffects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;

        if (canDrop)
        {
            var point = e.GetPosition(Viewport);
            var worldPos = new Point(
                (point.X - _panTransform.X) / _zoom,
                (point.Y - _panTransform.Y) / _zoom);

            var snappedChanged = Snap(worldPos.X) != Snap(_cursorPosition.X) ||
                                 Snap(worldPos.Y) != Snap(_cursorPosition.Y) ||
                                 !_dragOverCanvas;
            _cursorPosition = worldPos;
            _dragOverCanvas = true;
            if (snappedChanged)
                Redraw();
        }

        e.Handled = true;
    }

    private void Canvas_Drop(object? sender, DragEventArgs e)
    {
        _dragOverCanvas = false;
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
        StatusText.Text = L("Placed_Single", component.Tag);
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
            StatusText.Text = L("Wire_Connected_Terminal");
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
        StatusText.Text = L("Wire_Junction_Created");
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
        if (_isPlacingPaste)
        {
            CommitPastedPlacement(_cursorPosition);
            e.Handled = true;
            return;
        }
        if (!_simulator.IsRunning && e.ClickCount == 2 && HitComponent(_cursorPosition) is { } timerComp &&
            timerComp.Kind is ComponentKind.TimerOnDelay or ComponentKind.TimerOffDelay or ComponentKind.TimerOnOffDelay)
        {
            EditTimerComponent(timerComp);
            e.Handled = true;
            return;
        }
        if (!_simulator.IsRunning && e.ClickCount == 2 && HitComponent(_cursorPosition) is { } breaker &&
            breaker.Kind is ComponentKind.Coil or ComponentKind.Lamp or ComponentKind.BlinkingLamp or ComponentKind.Breaker1P or ComponentKind.Breaker1PN or ComponentKind.Breaker2P or ComponentKind.Breaker3P or ComponentKind.Breaker3PN or ComponentKind.Rcd1PN or ComponentKind.Rcd3PN or ComponentKind.ThermalRelay or 
            ComponentKind.Contactor2P or ComponentKind.Contactor3P or ComponentKind.Contactor4P or ComponentKind.PushButtonNO or ComponentKind.PushButtonNC or ComponentKind.PushButtonNO_NC or ComponentKind.PushButtonCon or ComponentKind.ToggleSwitchNO or ComponentKind.ToggleSwitchNC or ComponentKind.ToggleSwitchNO_NC or 
            ComponentKind.ToggleSwitchCon or ComponentKind.CrossoverSwitch or ComponentKind.AuxContactNO or ComponentKind.AuxContactNC or ComponentKind.AuxContactNO_NC or ComponentKind.ConContact or ComponentKind.OverloadNC or ComponentKind.OverloadNO or ComponentKind.ThermalRelayAuxNC or ComponentKind.ThermalRelayAuxNO_NC or 
            ComponentKind.DcMotor or ComponentKind.AcMotor or ComponentKind.AcMotor1P or ComponentKind.TimerOnContactNO or ComponentKind.TimerOnContactNC or ComponentKind.TimerOffContactNO or ComponentKind.TimerOffContactNC or ComponentKind.TimerOnOffContactNO_NC or ComponentKind.Fuse or ComponentKind.Bell or ComponentKind.Siren)
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
                    StatusText.Text = L("Wire_Pick_Start");
                    return;
                }

                if (HitWire(_cursorPosition) is Guid targetWireId)
                {
                    ConnectWireToWire(targetWireId, _cursorPosition);
                    Redraw();
                    return;
                }

                LockWireCorner(_cursorPosition);
                StatusText.Text = L("Wire_Corner_Locked");
                Redraw();
                return;
            }

            if (_wireStart is null)
            {
                _wireWaypoints.Clear();
                _wireStart = terminal;
                var startComponent = _project.Components.First(c => c.Id == terminal.Value.ComponentId);
                StatusText.Text = L("Wire_Start_Selected", startComponent.Tag, TerminalLabel(startComponent, terminal.Value.TerminalId));
            }
            else if (_wireStart.Value.ComponentId == terminal.Value.ComponentId &&
                     _wireStart.Value.TerminalId == terminal.Value.TerminalId)
            {
                StatusText.Text = L("Wire_Pick_Other");
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
                StatusText.Text = L("Wire_Connected_Color", CurrentWireColorLabel().ToLowerInvariant());
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
            StatusText.Text = L("Placed_Active", c.Tag);
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
            StatusText.Text = L("Wire_Edit_Segment");
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
private async void EditTimerComponent(ComponentModel timer)
    {
        var editor = new Window
        {
            Title = L("Timer_Edit_Title"), Width = 330, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false
        };
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 6 };
        var tagInput = new TextBox { Text = timer.Tag };
        panel.Children.Add(new TextBlock { Text = L("Edit_Tag_Prompt") });
        panel.Children.Add(tagInput);

        CheckBox? tonCheck = null;
        TextBox? onInput = null;
        if (timer.Kind is ComponentKind.TimerOnDelay or ComponentKind.TimerOnOffDelay)
        {
            onInput = new TextBox { Text = timer.DelayOnSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) };
            if (timer.Kind == ComponentKind.TimerOnOffDelay)
            {
                tonCheck = new CheckBox { Content = "TON", IsChecked = timer.EnableTon, Margin = new Thickness(0, 6, 0, 0) };
                onInput.IsEnabled = timer.EnableTon;
                tonCheck.IsCheckedChanged += (_, _) => onInput.IsEnabled = tonCheck.IsChecked == true;
                panel.Children.Add(tonCheck);
            }
            panel.Children.Add(new TextBlock { Text = L("Timer_Delay_On"), Margin = new Thickness(0, timer.Kind == ComponentKind.TimerOnOffDelay ? 0 : 6, 0, 0) });
            panel.Children.Add(onInput);
        }

        CheckBox? tofCheck = null;
        TextBox? offInput = null;
        if (timer.Kind is ComponentKind.TimerOffDelay or ComponentKind.TimerOnOffDelay)
        {
            offInput = new TextBox { Text = timer.DelayOffSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) };
            if (timer.Kind == ComponentKind.TimerOnOffDelay)
            {
                tofCheck = new CheckBox { Content = "TOF", IsChecked = timer.EnableTof, Margin = new Thickness(0, 6, 0, 0) };
                offInput.IsEnabled = timer.EnableTof;
                tofCheck.IsCheckedChanged += (_, _) => offInput.IsEnabled = tofCheck.IsChecked == true;
                panel.Children.Add(tofCheck);
            }
            panel.Children.Add(new TextBlock { Text = L("Timer_Delay_Off"), Margin = new Thickness(0, timer.Kind == ComponentKind.TimerOnOffDelay ? 0 : 6, 0, 0) });
            panel.Children.Add(offInput);
        }

        var save = new Button
        {
            Content = L("Save_Action"), IsDefault = true, Width = 100,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        save.Click += (_, _) =>
        {
            PushUndoState();
            if (!string.IsNullOrWhiteSpace(tagInput.Text))
                timer.Tag = tagInput.Text.Trim();
            if (tonCheck is not null)
                timer.EnableTon = tonCheck.IsChecked == true;
            if (tofCheck is not null)
                timer.EnableTof = tofCheck.IsChecked == true;
            if (onInput is not null && double.TryParse(onInput.Text?.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var onSec))
                timer.DelayOnSeconds = Math.Clamp(onSec, 0, 3600);
            if (offInput is not null && double.TryParse(offInput.Text?.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var offSec))
                timer.DelayOffSeconds = Math.Clamp(offSec, 0, 3600);
            Redraw();
            editor.Close(true);
        };
        panel.Children.Add(save);
        editor.Content = panel;
        editor.Opened += (_, _) => { (onInput ?? offInput ?? tagInput).Focus(); (onInput ?? offInput ?? tagInput).SelectAll(); };
        await editor.ShowDialog(this);
    }

    private async void EditComponentTag(ComponentModel component)
    {
        var editor = new Window
        {
            Title = L("Edit_Tag_Title"), Width = 300, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        var input = new TextBox { Text = component.Tag, Margin = new Thickness(0, 0, 0, 10), MinWidth = 250 };
        var save = new Button { Content = L("Save_Action"), IsDefault = true, Width = 90, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) { PushUndoState(); component.Tag = input.Text.Trim(); Redraw(); } editor.Close(true); };
        panel.Children.Add(new TextBlock { Text = L("Edit_Tag_Prompt") }); panel.Children.Add(input); panel.Children.Add(save); editor.Content = panel;
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
        {
            case ComponentKind.LimitSwitchNO:
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
                StatusText.Text = L("Button_Pressed_Tip", c.Tag);
                break;
            case ComponentKind.ToggleSwitchNO:
            case ComponentKind.ToggleSwitchNC:
            case ComponentKind.ToggleSwitchNO_NC:
            case ComponentKind.ToggleSwitchCon:
            case ComponentKind.CrossoverSwitch:
                _simulator.ToggleContactSwitch(c.Id, _project);
                StatusText.Text = $"{c.Tag}: {_simulator.GetState(c.Id)}";
                break;
            case ComponentKind.Fuse:
            _simulator.ToggleFuse(c.Id, _project);
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
                StatusText.Text = L("Contact_Controlled_Tip", c.Tag, _simulator.GetState(c.Id));
                break;
            case ComponentKind.Coil:
            case ComponentKind.TimerOnDelay:
            case ComponentKind.TimerOffDelay:
            case ComponentKind.TimerOnOffDelay:
            case ComponentKind.Lamp:
            case ComponentKind.BlinkingLamp:
            case ComponentKind.Motor:
            case ComponentKind.DcMotor:
            case ComponentKind.AcMotor:
            case ComponentKind.AcMotor1P:
                StatusText.Text = L("Load_Controlled_Tip", c.Tag, _simulator.GetState(c.Id));
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
        CursorCoordsText.Text = $"X: {Snap(_cursorPosition.X):0}  Y: {Snap(_cursorPosition.Y):0}";

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
            _placingKind is not null || _wireMode || _isPlacingPaste || prevHover != _hoverTerminal)
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
                    StatusText.Text = L("Wire_Routed");
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
                StatusText.Text = L("Selection_Cleared");
            else
                StatusText.Text = L("Selection_Count", _selectedComponentIds.Count);
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

    private void ClearTerminalBatch()
    {
        _terminalBatchSource.Clear();
        _terminalBatchTarget.Clear();
        _terminalBatchSourceComponent = null;
        _terminalBatchTargetComponent = null;
        _terminalPressPosition = null;
    }

    private void HandleTerminalBatchClick((Guid ComponentId, string TerminalId) terminal)
    {
        if (_terminalBatchSource.Any(t => _project.Components.All(c => c.Id != t.ComponentId)) ||
            _terminalBatchTarget.Any(t => _project.Components.All(c => c.Id != t.ComponentId)))
        {
            ClearTerminalBatch();
        }

        if (_terminalBatchSourceComponent is null)
        {
            _terminalBatchSourceComponent = terminal.ComponentId;
            _terminalBatchSource.Add(terminal);
            StatusText.Text = L("Batch_Start");
            return;
        }

        if (_terminalBatchTargetComponent is null && terminal.ComponentId == _terminalBatchSourceComponent)
        {
            if (!_terminalBatchSource.Contains(terminal)) _terminalBatchSource.Add(terminal);
            StatusText.Text = L("Batch_First_Count", _terminalBatchSource.Count);
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
            StatusText.Text = L("Batch_Wired_Count", Math.Min(_terminalBatchSource.Count, _terminalBatchTarget.Count));
            ClearTerminalBatch();
            return;
        }
        StatusText.Text = L("Batch_Second_Progress", _terminalBatchTarget.Count, _terminalBatchSource.Count);
    }

    private List<((Guid ComponentId, string TerminalId) Source, (Guid ComponentId, string TerminalId) Target)> MatchTerminalGroups(
        IReadOnlyList<(Guid ComponentId, string TerminalId)> sourceInput,
        IReadOnlyList<(Guid ComponentId, string TerminalId)> targetInput)
    {
        var source = sourceInput
            .Select(t => (Terminal: t, Comp: _project.Components.FirstOrDefault(c => c.Id == t.ComponentId)))
            .Where(x => x.Comp is not null)
            .Select(x => (x.Terminal, Position: TerminalPosition(x.Comp!, x.Terminal.TerminalId)))
            .OrderBy(x => x.Position.Y).ThenBy(x => x.Position.X).ToArray();
        var targets = targetInput
            .Select(t => (Terminal: t, Comp: _project.Components.FirstOrDefault(c => c.Id == t.ComponentId)))
            .Where(x => x.Comp is not null)
            .Select(x => (x.Terminal, Position: TerminalPosition(x.Comp!, x.Terminal.TerminalId)))
            .OrderBy(x => x.Position.Y).ThenBy(x => x.Position.X).ToArray();

        var count = Math.Min(source.Length, targets.Length);
        if (count == 0) return new();

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

    // private void ClearTerminalBatch()
    // {
    //     _terminalBatchSource.Clear();
    //     _terminalBatchTarget.Clear();
    //     _terminalBatchSourceComponent = null;
    //     _terminalBatchTargetComponent = null;
    //     _terminalPressPosition = null;
    // }

    // private void HandleTerminalBatchClick((Guid ComponentId, string TerminalId) terminal)
    // {
    //     if (_terminalBatchSource.Any(t => _project.Components.All(c => c.Id != t.ComponentId)) ||
    //         _terminalBatchTarget.Any(t => _project.Components.All(c => c.Id != t.ComponentId)))
    //     {
    //         ClearTerminalBatch();
    //     }

    //     if (_terminalBatchSourceComponent is null)
    //     {
    //         _terminalBatchSourceComponent = terminal.ComponentId;
    //         _terminalBatchSource.Add(terminal);
    //         StatusText.Text = "Начальная клемма выбрана. Можно выбрать ещё клеммы этого устройства, затем клеммы второго устройства в том же порядке.";
    //         return;
    //     }

    //     if (_terminalBatchTargetComponent is null && terminal.ComponentId == _terminalBatchSourceComponent)
    //     {
    //         if (!_terminalBatchSource.Contains(terminal)) _terminalBatchSource.Add(terminal);
    //         StatusText.Text = $"Выбрано клемм первого устройства: {_terminalBatchSource.Count}. Теперь выбери столько же клемм второго устройства в нужном порядке.";
    //         return;
    //     }

    //     if (_terminalBatchTargetComponent is null) _terminalBatchTargetComponent = terminal.ComponentId;
    //     if (terminal.ComponentId != _terminalBatchTargetComponent || _terminalBatchTarget.Contains(terminal)) return;
    //     _terminalBatchTarget.Add(terminal);

    //     if (_terminalBatchSource.Count == 1 || _terminalBatchTarget.Count >= _terminalBatchSource.Count)
    //     {
    //         PushUndoState();
    //         var pairs = MatchTerminalGroups(_terminalBatchSource, _terminalBatchTarget);
    //         foreach (var (source, target) in pairs)
    //         {
    //             var a = _project.Components.FirstOrDefault(c => c.Id == source.ComponentId);
    //             var b = _project.Components.FirstOrDefault(c => c.Id == target.ComponentId);
    //             if (a is null || b is null) continue;
    //             var path = BuildWirePathAvoidingElements(TerminalPosition(a, source.TerminalId), TerminalPosition(b, target.TerminalId), a.Id, b.Id);
    //             _project.Wires.Add(new WireModel
    //             {
    //                 StartComponentId = a.Id, StartTerminalId = source.TerminalId,
    //                 EndComponentId = b.Id, EndTerminalId = target.TerminalId,
    //                 ColorKey = _selectedWireColorKey,
    //                 Waypoints = path.Skip(1).Take(Math.Max(0, path.Count - 2)).Select(p => new WireWaypoint { X = p.X, Y = p.Y }).ToList()
    //             });
    //         }
    //         StatusText.Text = $"Проложено проводов: {Math.Min(_terminalBatchSource.Count, _terminalBatchTarget.Count)}.";
    //         ClearTerminalBatch();
    //         return;
    //     }
    //     StatusText.Text = $"Выбрано клемм второго устройства: {_terminalBatchTarget.Count} из {_terminalBatchSource.Count}.";
    // }

    // private List<((Guid ComponentId, string TerminalId) Source, (Guid ComponentId, string TerminalId) Target)> MatchTerminalGroups(
    //     IReadOnlyList<(Guid ComponentId, string TerminalId)> sourceInput,
    //     IReadOnlyList<(Guid ComponentId, string TerminalId)> targetInput)
    // {
    //     var source = sourceInput
    //         .Select(t => (Terminal: t, Comp: _project.Components.FirstOrDefault(c => c.Id == t.ComponentId)))
    //         .Where(x => x.Comp is not null)
    //         .Select(x => (x.Terminal, Position: TerminalPosition(x.Comp!, x.Terminal.TerminalId)))
    //         .OrderBy(x => x.Position.Y).ThenBy(x => x.Position.X).ToArray();
    //     var targets = targetInput
    //         .Select(t => (Terminal: t, Comp: _project.Components.FirstOrDefault(c => c.Id == t.ComponentId)))
    //         .Where(x => x.Comp is not null)
    //         .Select(x => (x.Terminal, Position: TerminalPosition(x.Comp!, x.Terminal.TerminalId)))
    //         .OrderBy(x => x.Position.Y).ThenBy(x => x.Position.X).ToArray();

    //     var count = Math.Min(source.Length, targets.Length);
    //     if (count == 0) return new();

    //     var used = new bool[targets.Length];
    //     var current = new int[count];
    //     var best = new int[count];
    //     var bestCost = double.PositiveInfinity;
    //     void Search(int index, double cost)
    //     {
    //         if (index == count)
    //         {
    //             if (cost < bestCost) { bestCost = cost; Array.Copy(current, best, count); }
    //             return;
    //         }
    //         for (var j = 0; j < targets.Length; j++)
    //         {
    //             if (used[j]) continue;
    //             var nextCost = cost + Math.Abs(source[index].Position.X - targets[j].Position.X) + Math.Abs(source[index].Position.Y - targets[j].Position.Y);
    //             if (nextCost >= bestCost) continue;
    //             used[j] = true; current[index] = j;
    //             Search(index + 1, nextCost);
    //             used[j] = false;
    //         }
    //     }
    //     Search(0, 0);
    //     return Enumerable.Range(0, count).Select(i => (source[i].Terminal, targets[best[i]].Terminal)).ToList();
    // }

    private void Canvas_RightDown(PointerPressedEventArgs e)
    {
        var point = GetWorldPosition(e);
        if (!_simulator.IsRunning)
        {
            var junctionRadius = Math.Clamp(12.0 / _zoom, 9.0, 24.0);
            var junction = _project.Components
                .Where(c => c.Kind == ComponentKind.WireJunction && Len(new Point(c.X, c.Y) - point) <= junctionRadius)
                .OrderBy(c => Len(new Point(c.X, c.Y) - point))
                .FirstOrDefault();

            if (junction is not null)
            {
                _selected = junction;
                _selectedComponentIds.Clear();
                _selectedComponentIds.Add(junction.Id);
                _selectedWireId = null;
                Redraw();
                BuildJunctionMenu(junction).Open(SchematicCanvas);
                e.Handled = true;
                return;
            }
        }

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
   private ContextMenu BuildJunctionMenu(ComponentModel junction)
    {
        var menu = new ContextMenu { Placement = PlacementMode.Pointer };

        var deleteItem = new MenuItem { Header = L("Menu_Delete_Junction") };
        deleteItem.Click += (_, _) =>
        {
            if (_simulator.IsRunning) return;

            var attached = _project.Wires
                .Where(w => w.StartComponentId == junction.Id || w.EndComponentId == junction.Id)
                .ToList();

            PushUndoState();

            if (attached.Count >= 2)
            {
                // Находим пару проводов для склейки (при 3+ проводах приоритет у лежащих на одной прямой и одного цвета)
                (WireModel W1, WireModel W2)? bestPair = null;
                double bestScore = double.NegativeInfinity;

                Point NeighborDirection(WireModel w)
                {
                    var isStart = w.StartComponentId == junction.Id;
                    Point neighbor;
                    if (w.Waypoints is { Count: > 0 })
                    {
                        var wp = isStart ? w.Waypoints[0] : w.Waypoints[^1];
                        neighbor = new Point(wp.X, wp.Y);
                    }
                    else
                    {
                        var otherId = isStart ? w.EndComponentId : w.StartComponentId;
                        var otherTerm = isStart ? w.EndTerminalId : w.StartTerminalId;
                        var otherComp = _project.Components.FirstOrDefault(c => c.Id == otherId);
                        neighbor = otherComp is not null ? TerminalPosition(otherComp, otherTerm) : new Point(junction.X, junction.Y);
                    }
                    var dx = neighbor.X - junction.X;
                    var dy = neighbor.Y - junction.Y;
                    var len = Math.Sqrt(dx * dx + dy * dy);
                    return len > 0.01 ? new Point(dx / len, dy / len) : new Point(0, 0);
                }

                for (var i = 0; i < attached.Count; i++)
                {
                    for (var j = i + 1; j < attached.Count; j++)
                    {
                        var a = attached[i];
                        var b = attached[j];
                        var dirA = NeighborDirection(a);
                        var dirB = NeighborDirection(b);
                        // Противоположные направления дают скалярное произведение -1 => score выше
                        var dot = dirA.X * dirB.X + dirA.Y * dirB.Y;
                        var colorBonus = string.Equals(a.ColorKey, b.ColorKey, StringComparison.OrdinalIgnoreCase) ? 0.5 : 0.0;
                        var score = -dot + colorBonus;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestPair = (a, b);
                        }
                    }
                }

                if (bestPair is { } pair)
                {
                    var w1 = pair.W1;
                    var w2 = pair.W2;

                    var (otherStartId, otherStartTerm, w1Points) = w1.EndComponentId == junction.Id
                        ? (w1.StartComponentId, w1.StartTerminalId, (w1.Waypoints ?? new()).Select(p => new Point(p.X, p.Y)).ToList())
                        : (w1.EndComponentId, w1.EndTerminalId, (w1.Waypoints ?? new()).AsEnumerable().Reverse().Select(p => new Point(p.X, p.Y)).ToList());

                    var (otherEndId, otherEndTerm, w2Points) = w2.StartComponentId == junction.Id
                        ? (w2.EndComponentId, w2.EndTerminalId, (w2.Waypoints ?? new()).Select(p => new Point(p.X, p.Y)).ToList())
                        : (w2.StartComponentId, w2.StartTerminalId, (w2.Waypoints ?? new()).AsEnumerable().Reverse().Select(p => new Point(p.X, p.Y)).ToList());

                    var startComp = _project.Components.FirstOrDefault(c => c.Id == otherStartId);
                    var endComp = _project.Components.FirstOrDefault(c => c.Id == otherEndId);

                    if (startComp is not null && endComp is not null &&
                        !(otherStartId == otherEndId && otherStartTerm == otherEndTerm))
                    {
                        var pStart = TerminalPosition(startComp, otherStartTerm);
                        var pEnd = TerminalPosition(endComp, otherEndTerm);
                        var via = new List<Point>();
                        via.AddRange(w1Points);
                        via.Add(new Point(junction.X, junction.Y));
                        via.AddRange(w2Points);

                        var fullPath = BuildWirePath(pStart, pEnd, via);
                        var mergedWaypoints = fullPath.Skip(1).Take(Math.Max(0, fullPath.Count - 2))
                            .Select(p => new WireWaypoint { X = p.X, Y = p.Y }).ToList();

                        _project.Wires.Add(new WireModel
                        {
                            StartComponentId = otherStartId,
                            StartTerminalId = otherStartTerm,
                            EndComponentId = otherEndId,
                            EndTerminalId = otherEndTerm,
                            ColorKey = w1.ColorKey,
                            Waypoints = mergedWaypoints
                        });
                    }
                }
            }

            _project.Wires.RemoveAll(w => w.StartComponentId == junction.Id || w.EndComponentId == junction.Id);
            _project.Components.Remove(junction);
            PruneOrphanWireJunctions();
            _selected = null;
            _selectedComponentIds.Clear();
            _selectedWireId = null;
            StatusText.Text = attached.Count >= 2 ? L("Junction_Merged") : L("Junction_Deleted");
            menu.Close();
            Redraw();
        };
        menu.Items.Add(deleteItem);

        return menu;
    }
    private ContextMenu BuildExistingWireColorMenu(WireModel wire, Point point)
    {
        var menu = new ContextMenu
        {
            Placement = PlacementMode.Pointer
        };

        var heading = new MenuItem { Header = L("Wire_Color"), IsEnabled = false };
        menu.Items.Add(heading);
        menu.Items.Add(new Separator());

        foreach (var color in WireColors)
        {
            var colorName = L(color.LocKey);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border
            {
                Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0),
                Background = Brush(color.Hex),
                BorderBrush = Brush("#CBD5E1"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3)
            });
            row.Children.Add(new TextBlock { Text = colorName, VerticalAlignment = VerticalAlignment.Center });
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
                WireColorDot.Fill = Brush(WireColorHex(_selectedWireColorKey));
                _selectedWireId = null;
                _selected = null;
                _selectedComponentIds.Clear();
                StatusText.Text = L("Wire_Selected_Color", colorName);
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

        menu.Items.Add(new Separator());
        var deleteWireItem = new MenuItem { Header = L("Menu_Delete_Wire") };
        deleteWireItem.Click += (_, _) =>
        {
            if (_simulator.IsRunning) return;
            PushUndoState();
            _project.Wires.RemoveAll(w => w.Id == wire.Id);
            PruneOrphanWireJunctions();
            _selectedWireId = null;
            _selected = null;
            _selectedComponentIds.Clear();
            StatusText.Text = L("Wire_Deleted");
            menu.Close();
            Redraw();
        };
        menu.Items.Add(deleteWireItem);

        return menu;
    }

    private ContextMenu BuildLampGlowColorMenu(ComponentModel lamp, Point point)
    {
        var menu = new ContextMenu { Placement = PlacementMode.Pointer };
        menu.Items.Add(new MenuItem { Header = L("Glow_Color"), IsEnabled = false });
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
            row.Children.Add(new TextBlock { Text = L(color.LocKey), VerticalAlignment = VerticalAlignment.Center });
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
        if (ctrl && e.Key == Key.C)
        {
            CopySelected();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.V)
        {
            StartPastePlacement();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.X)
        {
            CutSelected();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.C)
        {
            CopySelected();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.V)
        {
            StartPastePlacement();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.D)
        {
            CopySelected();
            StartPastePlacement();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.D) // Дублирование в 1 клик
        {
            CopySelected();
            StartPastePlacement();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Z && ctrl)
        {
            if (!_simulator.IsRunning) UndoLastAction();
            else StatusText.Text = L("Sim_Stop_Before_Undo");
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
                EnableTon = c.EnableTon,
                EnableTof = c.EnableTof,
                DelayOnSeconds = c.DelayOnSeconds,
                DelayOffSeconds = c.DelayOffSeconds,
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
        if (_activeTab is not null && !_activeTab.IsDirty)
        {
            _activeTab.IsDirty = true;
            RebuildTabs();
        }
    }

    private void UndoLastAction()
    {
        if (_undoStack.Count == 0)
        {
            StatusText.Text = L("Nothing_To_Undo");
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
        ClearTerminalBatch();
        Redraw();
        StatusText.Text = L("Undo_Done");
    }

    private void CancelTool()
    {
        _isPlacingPaste = false;
        _placingKind = null; _wireMode = false; _wireStart = null;
        _wireWaypoints.Clear();
        _terminalBatchSource.Clear(); _terminalBatchTarget.Clear();
        _terminalBatchSourceComponent = null; _terminalBatchTargetComponent = null;
        _terminalPressPosition = null; _selected = null; _selectedComponentIds.Clear(); _selectedWireId = null;
        _movingWireId = null; _wireMoveOriginalPath = null; _wireMoveUndoSaved = false;
        _hoverTerminal = null;
        _selectingArea = false;
        ReleaseCapture();
        Redraw(); StatusText.Text = L("Tool_Cancelled");
    }

    private ComponentModel? HitComponent(Point point) => _project.Components.AsEnumerable().Reverse()
        .FirstOrDefault(c =>
        {
            if (c.Kind == ComponentKind.WireJunction)
                return !_simulator.IsRunning && new Rect(c.X - 8, c.Y - 8, 16, 16).Contains(point);

            var poles = c.Kind switch
            {
                ComponentKind.Breaker1PN or ComponentKind.Breaker2P or ComponentKind.Rcd1PN => 2,
                ComponentKind.Breaker3P or ComponentKind.Contactor3P => 3,
                ComponentKind.Contactor4P => 4,
                ComponentKind.Contactor2P => 2,
                ComponentKind.Breaker3PN or ComponentKind.Rcd3PN or ComponentKind.ThermalRelay or ComponentKind.SurgeProtector3PN => 4,
                _ => 1
            };
            var extent = poles == 1 ? 48 : 25.0 * (poles - 1) + 48;
            return new Rect(c.X - extent, c.Y - 34, extent * 2, 68).Contains(point);
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
            ClearTerminalBatch();
            Redraw(); StatusText.Text = L("Wire_Deleted");
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
        ClearTerminalBatch();
        Redraw();
        StatusText.Text = L("Elements_Deleted", ids.Count);
    }

    private void PruneOrphanWireJunctions()
    {
        bool changed;
        do
        {
            changed = false;
            var junctions = _project.Components
                .Where(c => c.Kind == ComponentKind.WireJunction)
                .ToList();

            foreach (var junction in junctions)
            {
                var degree = _project.Wires.Count(w =>
                    w.StartComponentId == junction.Id || w.EndComponentId == junction.Id);

                if (degree <= 1)
                {
                    if (_project.Wires.RemoveAll(w =>
                            w.StartComponentId == junction.Id || w.EndComponentId == junction.Id) > 0)
                        changed = true;
                    if (_project.Components.Remove(junction))
                        changed = true;
                }
            }
        }
        while (changed);

        // На случай замкнутого висящего контура из одних узлов без единого компонента
        var reachable = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        foreach (var comp in _project.Components.Where(c => c.Kind != ComponentKind.WireJunction))
        {
            if (reachable.Add(comp.Id))
                queue.Enqueue(comp.Id);
        }
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var wire in _project.Wires)
            {
                if (wire.StartComponentId == id && reachable.Add(wire.EndComponentId))
                    queue.Enqueue(wire.EndComponentId);
                else if (wire.EndComponentId == id && reachable.Add(wire.StartComponentId))
                    queue.Enqueue(wire.StartComponentId);
            }
        }

        _project.Components.RemoveAll(c => c.Kind == ComponentKind.WireJunction && !reachable.Contains(c.Id));
        _project.Wires.RemoveAll(w =>
            _project.Components.All(c => c.Id != w.StartComponentId) ||
            _project.Components.All(c => c.Id != w.EndComponentId));
    }

    // private void RotateSelected_Click(object sender, RoutedEventArgs e) => RotateSelected();
    private void RotateSelected()
    {
        if (_simulator.IsRunning) { StatusText.Text = L("Sim_Stop_Before_Rotate"); return; }
        if (_placingKind is not null && _selected is null)
        {
            _placingRotation = (_placingRotation + 90) % 360;
            Redraw();
            StatusText.Text = L("Rotate_Preview", _placingRotation);
            return;
        }
        if (_selectedComponentIds.Count == 0 && _selected is not null)
            _selectedComponentIds.Add(_selected.Id);
        if (_selectedComponentIds.Count == 0) { StatusText.Text = L("Rotate_Select_First"); return; }

        PushUndoState();
        foreach (var component in _project.Components.Where(c => _selectedComponentIds.Contains(c.Id)))
            component.Rotation = (component.Rotation + 90) % 360;
        Redraw();
        StatusText.Text = L("Rotated_Count", _selectedComponentIds.Count);
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
            if (_isPlacingPaste && !_simulator.IsRunning && _clipboardComponents.Count > 0)
            {
                var targetCenter = new Point(Snap(_cursorPosition.X), Snap(_cursorPosition.Y));
                var deltaX = targetCenter.X - _clipboardCenter.X;
                var deltaY = targetCenter.Y - _clipboardCenter.Y;

                // Рисуем провода из буфера
                foreach (var w in _clipboardWires)
                {
                    var startComp = _clipboardComponents.FirstOrDefault(c => c.Id == w.StartComponentId);
                    var endComp = _clipboardComponents.FirstOrDefault(c => c.Id == w.EndComponentId);
                    if (startComp is null || endComp is null) continue;

                    var tempStart = new ComponentModel { X = Snap(startComp.X + deltaX), Y = Snap(startComp.Y + deltaY), Rotation = startComp.Rotation, Kind = startComp.Kind };
                    var tempEnd = new ComponentModel { X = Snap(endComp.X + deltaX), Y = Snap(endComp.Y + deltaY), Rotation = endComp.Rotation, Kind = endComp.Kind };

                    var pStart = TerminalPosition(tempStart, w.StartTerminalId);
                    var pEnd = TerminalPosition(tempEnd, w.EndTerminalId);
                    var waypoints = (w.Waypoints ?? new List<WireWaypoint>()).Select(p => new Point(Snap(p.X + deltaX), Snap(p.Y + deltaY)));
                    var path = BuildWirePath(pStart, pEnd, waypoints);
                    DrawWirePath(path, WireColorHex(w.ColorKey), dashed: true);
                }

                foreach (var c in _clipboardComponents)
                {
                    var preview = new ComponentModel
                    {
                        Kind = c.Kind,
                        Tag = c.Tag,
                        X = Snap(c.X + deltaX),
                        Y = Snap(c.Y + deltaY),
                        Rotation = c.Rotation,
                        Phases = c.Phases,
                        Voltage = c.Voltage
                    };
                    DrawComponent(preview, Brush("#7896C4"), true);
                }
            }

            if (_simulator.IsRunning)
                foreach (var shortPoint in _simulator.ShortCircuitPoints)
                    DrawShortCircuitSpark(new Point(shortPoint.X, shortPoint.Y));

            var previewKind = _placingKind ?? (_dragOverCanvas ? _dragInFlight?.Kind : null);
            if (previewKind is not null && !_simulator.IsRunning)
            {
                var previewRotation = _placingKind is not null
                    ? _placingRotation
                    : (ComponentTerminals.IsPowerMarker(previewKind.Value) ? 90 : 0);

                var preview = new ComponentModel
                {
                    Kind = previewKind.Value,
                    Tag = "?",
                    X = Snap(_cursorPosition.X),
                    Y = Snap(_cursorPosition.Y),
                    Rotation = previewRotation,
                    Phases = previewKind == ComponentKind.AcSupply ? _pendingAcPhases : 1,
                    Voltage = previewKind == ComponentKind.AcSupply ? _pendingAcVoltage : 230
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
            StatusText.Text = L("Draw_Error", ex.Message);
        }
    }

    private string GetComponentColor(ComponentModel c)
    {
        if (!_simulator.IsRunning) return _darkTheme ? "#FFFFFF" : "#000000";

        var energizedColor = _darkTheme ? "#22C55E" : "#15803D";
        var inactiveLoadColor = _darkTheme ? "#CBD5E1" : "#64748B";
        var idleElementColor = _darkTheme ? "#E2E8F0" : "#334155";

        if (c.Kind is ComponentKind.Coil or ComponentKind.TimerOnDelay or ComponentKind.TimerOffDelay or ComponentKind.TimerOnOffDelay or ComponentKind.Lamp or ComponentKind.BlinkingLamp or ComponentKind.Bell or ComponentKind.Siren or ComponentKind.Motor)
            return _simulator.IsEnergized(c.Id) ? energizedColor : inactiveLoadColor;
        if (c.Kind is ComponentKind.AcSupply or ComponentKind.Supply24V)
            return _simulator.IsEnergized(c.Id) ? energizedColor : inactiveLoadColor;
        if (_simulator.IsEnergized(c.Id)) return energizedColor;
        return idleElementColor;
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
        var bodyObstacles = new List<Rect>();
        foreach (var c in _project.Components)
        {
            if (c.Kind == ComponentKind.WireJunction) continue;

            var poles = c.Kind switch
            {
                ComponentKind.Breaker1PN or ComponentKind.Breaker2P or ComponentKind.Rcd1PN or ComponentKind.Contactor2P => 2,
                ComponentKind.Breaker3P or ComponentKind.Contactor3P or ComponentKind.ThermalRelay => 3,
                ComponentKind.Breaker3PN or ComponentKind.Rcd3PN or ComponentKind.Contactor4P or ComponentKind.SurgeProtector3PN => 4,
                _ => 1
            };

            var poleHalf = poles > 1 ? ComponentTerminals.PoleSpacing * (poles - 1) / 2.0 : 0.0;
            if (c.Id == startId || c.Id == endId)
            {
                // Защищаем сам корпус подключаемых элементов и их остальные клеммы
                bodyObstacles.Add(new Rect(c.X - (poleHalf + 16), c.Y - 20, (poleHalf + 16) * 2, 40));
                foreach (var term in ComponentTerminals.Get(c))
                {
                    var tp = TerminalPosition(c, term.Id);
                    if (Len(tp - start) < 1.5 || Len(tp - end) < 1.5) continue;
                    bodyObstacles.Add(new Rect(tp.X - 5, tp.Y - 5, 10, 10));
                }
            }
            else
            {
                bodyObstacles.Add(new Rect(c.X - (poleHalf + 26), c.Y - 24, (poleHalf + 26) * 2, 48));
            }
        }

        var hWires = new List<(double Y, double X1, double X2)>();
        var vWires = new List<(double X, double Y1, double Y2)>();
        var xs = new List<double> { start.X, end.X, Snap(start.X - 10), Snap(start.X + 10), Snap(end.X - 10), Snap(end.X + 10) };
        var ys = new List<double> { start.Y, end.Y, Snap(start.Y - 10), Snap(start.Y + 10), Snap(end.Y - 10), Snap(end.Y + 10) };

        foreach (var r in bodyObstacles)
        {
            xs.Add(Snap(r.Left - 10)); xs.Add(Snap(r.Right + 10));
            ys.Add(Snap(r.Top - 10)); ys.Add(Snap(r.Bottom + 10));
        }

        foreach (var wire in _project.Wires)
        {
            var a = _project.Components.FirstOrDefault(c => c.Id == wire.StartComponentId);
            var b = _project.Components.FirstOrDefault(c => c.Id == wire.EndComponentId);
            if (a is null || b is null) continue;

            var route = BuildWirePath(
                TerminalPosition(a, wire.StartTerminalId),
                TerminalPosition(b, wire.EndTerminalId),
                wire.Waypoints?.Select(p => new Point(p.X, p.Y)));

            for (var i = 1; i < route.Count; i++)
            {
                var p1 = route[i - 1];
                var p2 = route[i];
                if (Math.Abs(p1.Y - p2.Y) < 0.5)
                {
                    hWires.Add((p1.Y, Math.Min(p1.X, p2.X), Math.Max(p1.X, p2.X)));
                    ys.Add(Snap(p1.Y - 10)); ys.Add(Snap(p1.Y + 10));
                    xs.Add(Snap(p1.X)); xs.Add(Snap(p2.X));
                }
                else if (Math.Abs(p1.X - p2.X) < 0.5)
                {
                    vWires.Add((p1.X, Math.Min(p1.Y, p2.Y), Math.Max(p1.Y, p2.Y)));
                    xs.Add(Snap(p1.X - 10)); xs.Add(Snap(p1.X + 10));
                    ys.Add(Snap(p1.Y)); ys.Add(Snap(p2.Y));
                }
            }
        }

        static List<double> UniqueSorted(List<double> raw)
        {
            raw.Sort();
            var list = new List<double>(raw.Count);
            foreach (var v in raw)
                if (list.Count == 0 || Math.Abs(v - list[^1]) > 0.5)
                    list.Add(v);
            return list;
        }

        xs = UniqueSorted(xs);
        ys = UniqueSorted(ys);

        var width = xs.Count;
        var height = ys.Count;
        var startX = xs.FindIndex(x => Math.Abs(x - start.X) < 0.6);
        var startY = ys.FindIndex(y => Math.Abs(y - start.Y) < 0.6);
        var endX = xs.FindIndex(x => Math.Abs(x - end.X) < 0.6);
        var endY = ys.FindIndex(y => Math.Abs(y - end.Y) < 0.6);
        if (startX < 0 || startY < 0 || endX < 0 || endY < 0)
            return BuildWirePath(start, end, null);

        var startIdx = startX + startY * width;
        var endIdx = endX + endY * width;
        var total = width * height;

        // 0 = старт, 1 = горизонтально, 2 = вертикально
        var dist = new double[total * 3];
        var prevState = new int[total * 3];
        Array.Fill(dist, double.PositiveInfinity);
        Array.Fill(prevState, -1);

        var queue = new PriorityQueue<int, double>();
        var startState = startIdx * 3;
        dist[startState] = 0;
        queue.Enqueue(startState, 0);

        int bestEndState = -1;
        double bestEndCost = double.PositiveInfinity;

        while (queue.TryDequeue(out var state, out var cost))
        {
            if (cost > dist[state] + 0.001) continue;
            var node = state / 3;
            var dir = state % 3;

            if (node == endIdx)
            {
                bestEndState = state;
                bestEndCost = cost;
                break;
            }

            var cx = node % width;
            var cy = node / width;
            var a = new Point(xs[cx], ys[cy]);

            void TryNeighbor(int nx, int ny, int nextDir)
            {
                if (nx < 0 || nx >= width || ny < 0 || ny >= height) return;
                var nextNode = nx + ny * width;
                var b = new Point(xs[nx], ys[ny]);

                if (bodyObstacles.Any(r => SegmentCrossesRect(a, b, r, start, end))) return;

                var stepCost = Len(b - a);
                if (dir != 0 && dir != nextDir)
                    stepCost += 60.0; // штраф за лишний поворот (убирает зигзаги)

                if (nextDir == 1)
                {
                    var x1 = Math.Min(a.X, b.X);
                    var x2 = Math.Max(a.X, b.X);
                    var y = a.Y;
                    foreach (var hw in hWires)
                    {
                        if (Math.Abs(hw.Y - y) < 4.0 && Math.Min(x2, hw.X2) - Math.Max(x1, hw.X1) > 1.0)
                        {
                            var touchingEndpointOnly = (Len(a - start) < 1 && x2 - x1 <= 6) || (Len(b - end) < 1 && x2 - x1 <= 6);
                            if (!touchingEndpointOnly) stepCost += 5000.0; // не накладываться на другой провод
                        }
                    }
                    foreach (var vw in vWires)
                    {
                        if (vw.X > x1 + 1.0 && vw.X < x2 - 1.0 && y > vw.Y1 + 1.0 && y < vw.Y2 - 1.0)
                            stepCost += 30.0; // допустимое пересечение под 90°
                    }
                }
                else
                {
                    var y1 = Math.Min(a.Y, b.Y);
                    var y2 = Math.Max(a.Y, b.Y);
                    var x = a.X;
                    foreach (var vw in vWires)
                    {
                        if (Math.Abs(vw.X - x) < 4.0 && Math.Min(y2, vw.Y2) - Math.Max(y1, vw.Y1) > 1.0)
                        {
                            var touchingEndpointOnly = (Len(a - start) < 1 && y2 - y1 <= 6) || (Len(b - end) < 1 && y2 - y1 <= 6);
                            if (!touchingEndpointOnly) stepCost += 5000.0; // не накладываться на другой провод
                        }
                    }
                    foreach (var hw in hWires)
                    {
                        if (hw.Y > y1 + 1.0 && hw.Y < y2 - 1.0 && x > hw.X1 + 1.0 && x < hw.X2 - 1.0)
                            stepCost += 30.0; // допустимое пересечение под 90°
                    }
                }

                var nextState = nextNode * 3 + nextDir;
                var nextCost = cost + stepCost;
                if (nextCost < dist[nextState])
                {
                    dist[nextState] = nextCost;
                    prevState[nextState] = state;
                    queue.Enqueue(nextState, nextCost);
                }
            }

            TryNeighbor(cx - 1, cy, 1);
            TryNeighbor(cx + 1, cy, 1);
            TryNeighbor(cx, cy - 1, 2);
            TryNeighbor(cx, cy + 1, 2);
        }

        if (bestEndState < 0 || double.IsPositiveInfinity(bestEndCost))
            return BuildWirePath(start, end, null);

        var path = new List<Point>();
        for (var s = bestEndState; s >= 0; s = prevState[s])
        {
            var node = s / 3;
            path.Add(node == startIdx ? start : node == endIdx ? end : new Point(xs[node % width], ys[node / width]));
            if (node == startIdx) break;
        }
        path.Reverse();

        for (var i = path.Count - 2; i > 0; i--)
        {
            var a = path[i - 1]; var b = path[i]; var c = path[i + 1];
            if ((Math.Abs(a.X - b.X) < 0.5 && Math.Abs(b.X - c.X) < 0.5) ||
                (Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(b.Y - c.Y) < 0.5))
                path.RemoveAt(i);
        }
        return path;
    }

    private static bool SegmentCrossesRect(Point a, Point b, Rect r, Point start, Point end)
    {
        // Если край отрезка — сама стартовая или конечная клемма, не считаем это столкновением
        if (Len(a - start) < 0.5 || Len(b - start) < 0.5 || Len(a - end) < 0.5 || Len(b - end) < 0.5)
        {
            var mid = new Point((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
            if (!r.Contains(mid)) return false;
        }

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
            double distance;
            if (flowDot.TotalLength < spacing + inset * 2)
            {
                var span = Math.Max(0.1, flowDot.TotalLength - inset * 2);
                distance = inset + (flowDot.BaseDistance - inset + phase) % span;
            }
            else
            {
                distance = flowDot.BaseDistance + phase;
                if (distance >= flowDot.TotalLength - inset)
                {
                    flowDot.Element.IsVisible = false;
                    continue;
                }
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
            
            case ComponentKind.Bell:
            {
                var bellOn = !preview && _simulator.IsRunning && _simulator.IsEnergized(c.Id);
                var bellStroke = bellOn ? Brush("#16A34A") : stroke;
                AddLine(group, 50, 2, 50, 16, bellStroke, 2);
                AddLine(group, 50, 48, 50, 62, bellStroke, 2);
                AddLine(group, 50, 16, 50, 48, bellStroke, 2);
                group.Children.Add(new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse("M 50,16 A 16,16 0 0,1 50,48 Z"),
                    Stroke = bellStroke,
                    StrokeThickness = 2,
                    Fill = bellOn ? Brush("#DCFCE7") : Brushes.Transparent
                });
                AddLine(group, 41, 32, 50, 32, bellStroke, 1.6);
                group.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = bellStroke });
                Canvas.SetLeft(group.Children[^1], 36); Canvas.SetTop(group.Children[^1], 29);
                if (bellOn)
                {
                    group.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M 71,23 Q 77,32 71,41"), Stroke = bellStroke, StrokeThickness = 1.8, Fill = Brushes.Transparent });
                    group.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M 76,18 Q 84,32 76,46"), Stroke = bellStroke, StrokeThickness = 1.8, Fill = Brushes.Transparent });
                }
                break;
            }
            case ComponentKind.Siren:
            {
                var sirenOn = !preview && _simulator.IsRunning && _simulator.IsEnergized(c.Id);
                var sirenStroke = sirenOn ? Brush("#16A34A") : stroke;
                AddLine(group, 50, 2, 50, 18, sirenStroke, 2);
                AddLine(group, 50, 46, 50, 62, sirenStroke, 2);
                group.Children.Add(new Rectangle
                {
                    Width = 14, Height = 28,
                    Stroke = sirenStroke, StrokeThickness = 2,
                    Fill = sirenOn ? Brush("#DCFCE7") : Brushes.Transparent
                });
                Canvas.SetLeft(group.Children[^1], 42); Canvas.SetTop(group.Children[^1], 18);
                group.Children.Add(new Polygon
                {
                    Points = new PointCollection { new(56, 24), new(73, 14), new(73, 50), new(56, 40) },
                    Stroke = sirenStroke, StrokeThickness = 2,
                    Fill = sirenOn ? Brush("#DCFCE7") : Brushes.Transparent
                });
                if (sirenOn)
                {
                    group.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M 78,23 Q 84,32 78,41"), Stroke = sirenStroke, StrokeThickness = 1.8, Fill = Brushes.Transparent });
                    group.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M 83,18 Q 91,32 83,46"), Stroke = sirenStroke, StrokeThickness = 1.8, Fill = Brushes.Transparent });
                }
                break;
            }

            case ComponentKind.LimitSwitchNO:
            case ComponentKind.LimitSwitchNC:
                var isLimitNC = c.Kind == ComponentKind.LimitSwitchNC;
                var limitContactClosed = preview ? isLimitNC : (_simulator.IsRunning ? _simulator.GetContactClosed(c.Id, c.Kind) : isLimitNC);
                var num1 = isLimitNC ? "11" : "13";
                var num2 = isLimitNC ? "12" : "14";

                DrawContactSymbol(group, stroke, limitContactClosed, isLimitNC ? ComponentKind.AuxContactNC : ComponentKind.AuxContactNO, num1, num2, drawActuator: false);

                // Ролик концевика и шток (как на фото)
                var rollerShift = (limitContactClosed != isLimitNC) ? 3.0 : 0.0;
                group.Children.Add(new Line
                {
                    StartPoint = new Point(16 + rollerShift, 28), 
                    EndPoint = new Point(43 + rollerShift, 28),   
                    Stroke = stroke, StrokeThickness = 1.6,
                    StrokeDashArray = new DoubleCollection { 3.0, 2.0 }
                });
                group.Children.Add(new Ellipse { Width = 14, Height = 14, Stroke = stroke, StrokeThickness = 1.8, Fill = Brushes.Transparent });
                Canvas.SetLeft(group.Children[^1], 2 + rollerShift); // было 10
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
                var ncSwitchGroup = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(-20, 0) };
                DrawContactSymbol(ncSwitchGroup, stroke, !noPosition, ComponentKind.AuxContactNC, "1", "2", terminalNumberX: 24);
                group.Children.Add(ncSwitchGroup);
                var noSwitchGroup = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(20, 0) };
                DrawContactSymbol(noSwitchGroup, stroke, noPosition, ComponentKind.AuxContactNO, "3", "4", terminalNumberX: 69);
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
                DrawContactSymbol(group, stroke, contactClosed, ComponentKind.AuxContactNO, "97", "98", terminalNumberX: 58);
                DrawThermalRelayContactMark(group, stroke, overloadNoBladeShift);
                break;
            case ComponentKind.ThermalRelayAuxNC:
                DrawContactSymbol(group, stroke, contactClosed, ComponentKind.AuxContactNC, "95", "96", terminalNumberX: 58);
                DrawThermalRelayContactMark(group, stroke, contactClosed ? 0 : 3);
                break;
            case ComponentKind.ThermalRelayAuxNO_NC:
            {
                var tripped = !preview && _simulator.IsRunning && contactClosed;
                var ncRelayGroup = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(-20, 0) };
                DrawContactSymbol(ncRelayGroup, stroke, !tripped, ComponentKind.AuxContactNC, "95", "96", terminalNumberX: 56);
                group.Children.Add(ncRelayGroup);
                var noRelayGroup = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(20, 0) };
                DrawContactSymbol(noRelayGroup, stroke, tripped, ComponentKind.AuxContactNO, "97", "98", terminalNumberX: 59);
                group.Children.Add(noRelayGroup);
                DrawThermalRelayPairMark(group, stroke, tripped ? 3 : 0);
                break;
            }
            case ComponentKind.PushButtonNO_NC:
            {
                var pressed = !preview && _simulator.IsRunning && contactClosed;
                var ncButton = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(-20, 0) };
                DrawContactSymbol(ncButton, stroke, !pressed, ComponentKind.PushButtonNC, "11", "12", drawActuator: false);
                group.Children.Add(ncButton);
                var noButton = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(20, 0) };
                DrawContactSymbol(noButton, stroke, pressed, ComponentKind.PushButtonNO, "13", "14", drawActuator: false);
                group.Children.Add(noButton);
                var actuatorShift = pressed ? 3.0 : 0.0;
                group.Children.Add(new Line
                {
                    StartPoint = new Point(16 + actuatorShift, 28), // было 22
                    EndPoint = new Point(62 + actuatorShift, 28),   // было 58
                    Stroke = stroke, StrokeThickness = 1.7,
                    StrokeDashArray = new DoubleCollection { 3.0, 2.2 }, IsHitTestVisible = false
                });
                var actuatorLabel = new TextBlock
                {
                    Text = "E", Foreground = stroke, FontSize = 13,
                    FontStyle = FontStyle.Italic, IsHitTestVisible = false, Tag = "NoZoom"
                };
                Canvas.SetLeft(actuatorLabel, 6 + actuatorShift); // было 12
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
            case ComponentKind.TimerOnContactNO:
            case ComponentKind.TimerOnContactNC:
            {
                var isNc = c.Kind == ComponentKind.TimerOnContactNC;
                DrawContactSymbol(group, stroke, contactClosed,
                    isNc ? ComponentKind.AuxContactNC : ComponentKind.AuxContactNO,
                    isNc ? "5" : "7", isNc ? "6" : "8", terminalNumberX: 56);
                AddLine(group, 30, 28, 45, 28, stroke, 1.5);
                AddLine(group, 30, 31, 45, 31, stroke, 1.5);
                group.Children.Add(new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse("M 32,23 Q 24,29.5 32,36"),
                    Stroke = stroke, StrokeThickness = 1.7, Fill = Brushes.Transparent
                });
                break;
            }
            case ComponentKind.TimerOffContactNO:
            case ComponentKind.TimerOffContactNC:
            {
                var isNc = c.Kind == ComponentKind.TimerOffContactNC;
                DrawContactSymbol(group, stroke, contactClosed,
                    isNc ? ComponentKind.AuxContactNC : ComponentKind.AuxContactNO,
                    isNc ? "5" : "7", isNc ? "6" : "8", terminalNumberX: 31);
                AddLine(group, 54, 28, 69, 28, stroke, 1.5);
                AddLine(group, 54, 31, 69, 31, stroke, 1.5);
                group.Children.Add(new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse("M 67,23 Q 75,29.5 67,36"),
                    Stroke = stroke, StrokeThickness = 1.7, Fill = Brushes.Transparent
                });
                break;
            }
            case ComponentKind.TimerOnOffContactNO_NC:
            {
                var ncTimerPair = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(-20, 0) };
                DrawContactSymbol(ncTimerPair, stroke, !contactClosed, ComponentKind.AuxContactNC, "5", "6", terminalNumberX: 35);
                group.Children.Add(ncTimerPair);
                var noTimerPair = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(20, 0) };
                DrawContactSymbol(noTimerPair, stroke, contactClosed, ComponentKind.AuxContactNO, "7", "8", terminalNumberX: 56);
                group.Children.Add(noTimerPair);
                group.Children.Add(new Line { StartPoint = new Point(30, 29.5), EndPoint = new Point(64, 29.5), Stroke = stroke, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3.0, 2.2 } });
                AddLine(group, 15, 28, 30, 28, stroke, 1.5);
                AddLine(group, 15, 31, 30, 31, stroke, 1.5);
                group.Children.Add(new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse("M 13,23 Q 19,29.5 13,36 M 18,23 Q 12,29.5 18,36"),
                    Stroke = stroke, StrokeThickness = 1.6, Fill = Brushes.Transparent
                });
                break;
            }
            case ComponentKind.AuxContactNO_NC:
                var ncPair = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(-20, 0) };
                DrawContactSymbol(ncPair, stroke, !contactClosed, ComponentKind.AuxContactNC, "11", "12");
                group.Children.Add(ncPair);
                var noPair = new Canvas { Width = 100, Height = 64, RenderTransform = new TranslateTransform(20, 0) };
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
            case ComponentKind.TimerOnDelay:
            case ComponentKind.TimerOffDelay:
            case ComponentKind.TimerOnOffDelay:
                AddLine(group, 50, 2, 50, 20, stroke, 2);
                AddLine(group, 50, 44, 50, 62, stroke, 2);
                group.Children.Add(new Rectangle { Width = 52, Height = 24, Stroke = stroke, StrokeThickness = 2 });
                Canvas.SetLeft(group.Children[^1], 24); Canvas.SetTop(group.Children[^1], 20);
                if (c.Kind == ComponentKind.TimerOnDelay)
                {
                    AddLine(group, 40, 20, 40, 44, stroke, 1.8);
                    AddLine(group, 24, 20, 40, 44, stroke, 1.5);
                    AddLine(group, 40, 20, 24, 44, stroke, 1.5);
                }
                else if (c.Kind == ComponentKind.TimerOffDelay)
                {
                    group.Children.Add(new Rectangle { Width = 16, Height = 24, Fill = stroke, Stroke = stroke, StrokeThickness = 1.6 });
                    Canvas.SetLeft(group.Children[^1], 24); Canvas.SetTop(group.Children[^1], 20);
                }
                else
                {
                    group.Children.Add(new Rectangle { Width = 7, Height = 24, Fill = stroke, Stroke = stroke, StrokeThickness = 1.6 });
                    Canvas.SetLeft(group.Children[^1], 24); Canvas.SetTop(group.Children[^1], 20);
                    AddLine(group, 43, 20, 43, 44, stroke, 1.8);
                    AddLine(group, 31, 20, 43, 44, stroke, 1.5);
                    AddLine(group, 43, 20, 31, 44, stroke, 1.5);
                }
                group.Children.Add(new TextBlock { Text = "A1", Foreground = stroke, FontSize = 8 });
                Canvas.SetLeft(group.Children[^1], 54); Canvas.SetTop(group.Children[^1], 4);
                group.Children.Add(new TextBlock { Text = "A2", Foreground = stroke, FontSize = 8 });
                Canvas.SetLeft(group.Children[^1], 54); Canvas.SetTop(group.Children[^1], 47);

                var timerText = preview
                    ? ""
                    : _simulator.IsRunning && _simulator.GetTimerRemainingSeconds(c) > 0.05
                        ? $"{_simulator.GetTimerRemainingSeconds(c):0.0}s"
                        : c.Kind switch
                        {
                            ComponentKind.TimerOnDelay => $"{c.DelayOnSeconds:0.#}s",
                            ComponentKind.TimerOffDelay => $"{c.DelayOffSeconds:0.#}s",
                            _ => (c.EnableTon, c.EnableTof) switch
                            {
                                (true, true) => $"{c.DelayOnSeconds:0.#}/{c.DelayOffSeconds:0.#}s",
                                (true, false) => $"TON {c.DelayOnSeconds:0.#}s",
                                (false, true) => $"TOF {c.DelayOffSeconds:0.#}s",
                                _ => "0s"
                            }
                        };
                if (!string.IsNullOrEmpty(timerText))
                {
                    var timeBadge = new TextBlock
                    {
                        Text = timerText,
                        Foreground = _simulator.IsRunning && _simulator.IsTimerOutputActive(c.Id) ? Brush("#16A34A") : stroke,
                        FontSize = 8,
                        Width = 31,
                        TextAlignment = TextAlignment.Center,
                        Tag = "NoZoom"
                    };
                    Canvas.SetLeft(timeBadge, 43);
                    Canvas.SetTop(timeBadge, 26);
                    group.Children.Add(timeBadge);
                }
             break;
            case ComponentKind.CircuitBreaker:
            case ComponentKind.Fuse:
                AddLine(group, 50, 2, 50, 62, stroke, 2);

                group.Children.Add(new Rectangle
                {
                    Width = 14,
                    Height = 36,
                    Stroke = stroke,
                    StrokeThickness = 2,
                    Fill = (_simulator.IsRunning && _simulator.GetState(c.Id) == "Сработал") 
                        ? Brush("#EF4444") 
                        : Brushes.Transparent
                });
                Canvas.SetLeft(group.Children[^1], 43);
                Canvas.SetTop(group.Children[^1], 14);

                AddTerminalNumber(group, "1", 37, 2, stroke);
                AddTerminalNumber(group, "2", 37, 52, stroke);

                AddCanvasLabel(c.Tag, c.X + 16, c.Y - 5, 9);
                break;
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
                    AddLine(group, 50, 2, 50, 16, lampStroke, 2),
                    AddLine(group, 50, 48, 50, 62, lampStroke, 2)
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
                    AddLine(group, 30, 2, 37, 14, motorStroke, 2);
                    AddLine(group, 50, 2, 50, 9, motorStroke, 2);
                    AddLine(group, 70, 2, 63, 14, motorStroke, 2);
                }
                else if (c.Kind == ComponentKind.AcMotor1P)
                {
                    AddLine(group, 30, 2, 37, 14, motorStroke, 2);
                    AddLine(group, 70, 2, 63, 14, motorStroke, 2);
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
                else if (c.Kind is ComponentKind.TimerOnContactNO or ComponentKind.TimerOnContactNC or ComponentKind.TimerOffContactNO or ComponentKind.TimerOffContactNC)
                    AddCanvasLabel(c.Tag, c.X + 22, c.Y - 8, 9);
                else if (c.Kind == ComponentKind.TimerOnOffContactNO_NC)
                     AddCanvasLabel(c.Tag, c.X + 36, c.Y - 8, 9);
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
        else if (c.Kind is ComponentKind.TimerOnContactNO or ComponentKind.TimerOnContactNC or ComponentKind.TimerOffContactNO or ComponentKind.TimerOffContactNC)
            AddCanvasLabel(c.Tag, c.X + 28, c.Y - 8, 9);
        else if (c.Kind == ComponentKind.TimerOnOffContactNO_NC)
            AddCanvasLabel(c.Tag, c.X + 36, c.Y - 8, 9);

        if (c.Kind is ComponentKind.Lamp or ComponentKind.BlinkingLamp or ComponentKind.Bell or ComponentKind.Siren)
            AddCanvasLabel(c.Tag, c.X - 32, c.Y - 5, 12);
        if (c.Kind == ComponentKind.DcMotor)
            AddCanvasLabel(c.Tag, c.X - 3, c.Y + 20, 9);
        else if (c.Kind is ComponentKind.AcMotor or ComponentKind.AcMotor1P)
            AddCanvasLabel(c.Tag, c.X - 44, c.Y - 4, 9);
        else if (c.Kind == ComponentKind.Coil)
            AddCanvasLabel(c.Tag, c.X + 24, c.Y - 24, 9);
        else if (c.Kind is ComponentKind.TimerOnDelay or ComponentKind.TimerOffDelay or ComponentKind.TimerOnOffDelay)
            AddCanvasLabel(c.Tag, c.X - 48, c.Y - 7, 9);
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
            if (text.Tag?.ToString() == "NoZoom")
                continue;

            if (text.DataContext is not double baseFontSize)
            {
                baseFontSize = text.FontSize;
                text.DataContext = baseFontSize;
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
                AddLine(group, x, 2, x, 20, contactStroke, 1.5);
                DrawSwitchBlade(group, x, 20, 44, contactStroke, closed, 8, 1.5);
                AddLine(group, x, 44, x, 62, contactStroke, 1.5);
            }
            return;
        }

        if (kind == ComponentKind.ThermalRelay)
        {
            // Three-pole thermal relay with a shared rectangular housing and
            // one thermal element per phase, matching the supplied reference.
            var poleSpacing = ComponentTerminals.PoleSpacing;
            var firstPoleX = 50 - poleSpacing;
            const double housingTop = 18;
            const double housingBottom = 46;
            const double housingLeft = 19;
            const double housingWidth = 67;

            var relayDesignation = tag.StartsWith('-') ? tag : $"-{tag}";
            group.Children.Add(new TextBlock
            {
                Text = relayDesignation,
                Foreground = stroke,
                FontSize = FontSizeForZoom(8)
            });
            Canvas.SetLeft(group.Children[^1], -15);
            Canvas.SetTop(group.Children[^1], 24);
            AddLine(group, 6, 32, housingLeft, 32, stroke, 1.4);

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
            AddLine(group, 50, 2, 50, 16, stroke, 2);
            DrawSwitchBlade(group, 50, 17, 50, stroke, closed, 7, 1.7);
            // Moving contact is physically continuous with the straight lower terminal.
            AddLine(group, 50, 48, 50, 62, stroke, 2);
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

    private void BackToSubcategories_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_activeCategory))
            FilterPalette(_activeCategory);
    }

    private static void ApplyIconForeground(object? element, IBrush fg, IBrush bg)
    {
        if (element is Avalonia.Controls.Shapes.Shape shape)
        {
            if (shape.Fill is ISolidColorBrush fillBrush && fillBrush.Color != Colors.Transparent)
            {
                if (shape.Tag is "BgFill" || fillBrush.Color == Colors.White)
                {
                    shape.Tag = "BgFill";
                    shape.Fill = bg;
                }
                else
                {
                    shape.Fill = fg;
                }
            }
            if (shape.Stroke is ISolidColorBrush strokeBrush && strokeBrush.Color != Colors.Transparent)
                shape.Stroke = fg;
        }
        else if (element is TextBlock tb)
        {
            tb.Foreground = fg;
        }
        else if (element is Panel panel)
        {
            foreach (var child in panel.Children)
                ApplyIconForeground(child, fg, bg);
        }
    }
    private void UpdateCategoryButtonsAppearance()
    {
        var btnBg = _darkTheme ? Brush("#2A2F3A") : Brushes.White;
        var iconFg = _darkTheme ? Brush("#E2E8F0") : Brushes.Black;

        foreach (var button in CategoryPanel.Children.OfType<Button>())
        {
            button.Classes.Set("selected", false);
            button.ClearValue(BackgroundProperty);
            button.ClearValue(BorderBrushProperty);
            button.BorderThickness = new Thickness(1);
            button.Opacity = 1.0;
            ApplyIconForeground(button.Content, iconFg, btnBg);
        }
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
        AddLine(group, 50, 2, 50, 21, stroke, contactThickness);
        AddLine(group, 50, 38, 50, 62, stroke, contactThickness);

        var isNormallyClosed = kind is ComponentKind.PushButtonNC or ComponentKind.AuxContactNC
            or ComponentKind.ToggleSwitchNC or ComponentKind.OverloadNC;
        if (isNormallyClosed)
        {
            // Keep the NC blade fixed; pressing/opening it only removes the bridge.
            AddLine(group, 58, 21, 50, 38, stroke, contactThickness);
            if (closed) AddLine(group, 50, 21, 58, 21, stroke, contactThickness);
        }
        else if (closed)
        {
            // A closed NO uses the same appearance as a closed NC.
            AddLine(group, 56, 21, 50, 38, stroke, contactThickness);
            AddLine(group, 50, 21, 56, 21, stroke, contactThickness);
        }
        else
            AddLine(group, 38, 20, 50, 38, stroke, contactThickness);

        if (isPushButton && drawActuator)
        {
            // Pressing the button nudges the dashed actuator toward the contact.
            var actuatorShift = isPressed ? 3.0 : 0.0;
            group.Children.Add(new Line { StartPoint = new Point(14 + actuatorShift, 28), EndPoint = new Point(43 + actuatorShift, 28), Stroke = stroke, StrokeThickness = 1.7, StrokeDashArray = new DoubleCollection { 3.0, 2.2 }, IsHitTestVisible = false });

            var eLabel = new TextBlock
            {
                Text = "E",
                Foreground = stroke,
                FontSize = 13,
                FontStyle = FontStyle.Italic,
                Tag = "NoZoom"
            };
            Canvas.SetLeft(eLabel, 4 + actuatorShift);
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
        AddLine(group, 25 + shift, 20, 31 + shift, 20, stroke, 1.8); // было 33, 39
        AddLine(group, 25 + shift, 20, 25 + shift, 29, stroke, 1.8);
        AddLine(group, 31 + shift, 20, 31 + shift, 29, stroke, 1.8);
        group.Children.Add(new Line
        {
            StartPoint = new Point(31 + shift, 24),
            EndPoint = new Point(50 + shift, 24), // было 39 -> 58
            Stroke = stroke, StrokeThickness = 1.6,
            StrokeDashArray = new DoubleCollection { 2.0, 2.0 }, IsHitTestVisible = false
        });
        AddLine(group, 63, 22, 63, 29, stroke, 2.2); // было 71
        AddLine(group, 63, 29, 70, 29, stroke, 2.2); // было 71, 78
        AddLine(group, 70, 29, 70, 36, stroke, 2.2);
        AddLine(group, 70, 36, 63, 36, stroke, 2.2);
        AddLine(group, 63, 36, 63, 43, stroke, 2.2);
    }
    private static void DrawThermalRelayPairMark(Canvas group, IBrush stroke, double shift)
    {
        AddLine(group, 7 + shift, 25, 7 + shift, 34, stroke, 1.8);
        AddLine(group, 7 + shift, 25, 13 + shift, 25, stroke, 1.8);
        AddLine(group, 13 + shift, 25, 13 + shift, 34, stroke, 1.8);
        group.Children.Add(new Line
        {
            StartPoint = new Point(13 + shift, 29),
            EndPoint = new Point(64 + shift, 29), 
            Stroke = stroke, StrokeThickness = 1.6,
            StrokeDashArray = new DoubleCollection { 2.0, 2.0 }, IsHitTestVisible = false
        });
        AddLine(group, 83, 22, 83, 29, stroke, 2.2); 
        AddLine(group, 83, 29, 90, 29, stroke, 2.2);
        AddLine(group, 90, 29, 90, 36, stroke, 2.2);
        AddLine(group, 90, 36, 83, 36, stroke, 2.2);
        AddLine(group, 83, 36, 83, 43, stroke, 2.2);
    }

    private static void DrawCrossoverSwitch(Canvas group, IBrush stroke, bool crossed)
    {
        const double leftContactX = 25;
        const double rightContactX = 75;
        const double upperY = 22;
        const double lowerY = 42;
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
        AddLine(group, 30, 38, 30, 62, stroke, 2.2); 
        AddLine(group, 70, 38, 70, 62, stroke, 2.2);   
        AddLine(group, 30, 38, 38, 38, stroke, 2.2);   
        AddLine(group, 62, 38, 70, 38, stroke, 2.2); 
        AddLine(group, 50, 20, energized ? 62 : 38, 38, stroke, 2.2);

        if (isButton)
        {
            var actuatorShift = pressed ? 3.0 : 0.0;
            group.Children.Add(new Line { StartPoint = new Point(13 + actuatorShift, 28), EndPoint = new Point(43 + actuatorShift, 28), Stroke = stroke, StrokeThickness = 1.7, StrokeDashArray = new DoubleCollection { 3.0, 2.2 }, IsHitTestVisible = false });
            var eLabel = new TextBlock
            {
                Text = "E",
                Foreground = stroke,
                FontSize = 13,
                FontStyle = FontStyle.Italic,
                IsHitTestVisible = false,
                Tag = "NoZoom"
            };
            Canvas.SetLeft(eLabel, 3 + actuatorShift);
            Canvas.SetTop(eLabel, 18);
            group.Children.Add(eLabel);
        }

        AddTerminalNumber(group, "11", 54, 0, stroke);
        AddTerminalNumber(group, "12", 12, 39, stroke);
        AddTerminalNumber(group, "14", 73, 39, stroke);
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

    private static string FriendlyName(ComponentKind kind) => L(kind switch
    {
        ComponentKind.Bell => "Comp_Bell",
        ComponentKind.Siren => "Comp_Siren",
        ComponentKind.Fuse => "Comp_Fuse",
        ComponentKind.TimerOnContactNO => "Comp_TimerOnContactNO",
        ComponentKind.TimerOnContactNC => "Comp_TimerOnContactNC",
        ComponentKind.TimerOffContactNO => "Comp_TimerOffContactNO",
        ComponentKind.TimerOffContactNC => "Comp_TimerOffContactNC",
        ComponentKind.TimerOnOffContactNO_NC => "Comp_TimerOnOffContactNO_NC",
        ComponentKind.TimerOnDelay => "Comp_TimerOnDelay",
        ComponentKind.TimerOffDelay => "Comp_TimerOffDelay",
        ComponentKind.TimerOnOffDelay => "Comp_TimerOnOffDelay",
        ComponentKind.LimitSwitchNO => "Comp_LimitSwitchNO",
        ComponentKind.LimitSwitchNC => "Comp_LimitSwitchNC",
        ComponentKind.PushButtonNO => "Comp_PushButtonNO",
        ComponentKind.PushButtonNC => "Comp_PushButtonNC",
        ComponentKind.PushButtonNO_NC => "Comp_PushButtonNO_NC",
        ComponentKind.PushButtonCon => "Comp_PushButtonCon",
        ComponentKind.ToggleSwitchNO => "Comp_ToggleSwitchNO",
        ComponentKind.ToggleSwitchNC => "Comp_ToggleSwitchNC",
        ComponentKind.ToggleSwitchNO_NC => "Comp_ToggleSwitchNO_NC",
        ComponentKind.ToggleSwitchCon => "Comp_ToggleSwitchCon",
        ComponentKind.CrossoverSwitch => "Comp_CrossoverSwitch",
        ComponentKind.OverloadNO => "Comp_OverloadNO",
        ComponentKind.ThermalRelayAuxNC => "Comp_ThermalRelayAuxNC",
        ComponentKind.ThermalRelayAuxNO_NC => "Comp_ThermalRelayAuxNO_NC",
        ComponentKind.AuxContactNO => "Comp_AuxContactNO",
        ComponentKind.AuxContactNC => "Comp_AuxContactNC",
        ComponentKind.AuxContactNO_NC => "Comp_AuxContactNO_NC",
        ComponentKind.ConContact => "Comp_ConContact",
        ComponentKind.OverloadNC => "Comp_OverloadNC",
        ComponentKind.Coil => "Comp_Coil",
        ComponentKind.Supply24V => "Comp_Supply24V",
        ComponentKind.AcSupply => "Comp_AcSupply",
        ComponentKind.Lamp => "Comp_Lamp",
        ComponentKind.BlinkingLamp => "Comp_BlinkingLamp",
        ComponentKind.Motor => "Comp_Motor",
        ComponentKind.DcMotor => "Comp_DcMotor",
        ComponentKind.AcMotor => "Comp_AcMotor",
        ComponentKind.AcMotor1P => "Comp_AcMotor1P",
        ComponentKind.CircuitBreaker => "Comp_CircuitBreaker",
        ComponentKind.DifferentialBreaker => "Comp_DifferentialBreaker",
        ComponentKind.Breaker1P => "Comp_Breaker1P",
        ComponentKind.Breaker1PN => "Comp_Breaker1PN",
        ComponentKind.Breaker2P => "Comp_Breaker2P",
        ComponentKind.Breaker3P => "Comp_Breaker3P",
        ComponentKind.Breaker3PN => "Comp_Breaker3PN",
        ComponentKind.Rcd1PN => "Comp_Rcd1PN",
        ComponentKind.Rcd3PN => "Comp_Rcd3PN",
        ComponentKind.ThermalRelay => "Comp_ThermalRelay",
        ComponentKind.Contactor2P => "Comp_Contactor2P",
        ComponentKind.Contactor3P => "Comp_Contactor3P",
        ComponentKind.Contactor4P => "Comp_Contactor4P",
        ComponentKind.PowerPhase => "Comp_PowerPhase",
        ComponentKind.PowerNeutral => "Comp_PowerNeutral",
        ComponentKind.PowerEarth => "Comp_PowerEarth",
        ComponentKind.PowerPhaseNeutral => "Comp_PowerPhaseNeutral",
        ComponentKind.PowerPhaseNeutralEarth => "Comp_PowerPhaseNeutralEarth",
        ComponentKind.PowerThreePhase => "Comp_PowerThreePhase",
        ComponentKind.PowerThreePhaseEarth => "Comp_PowerThreePhaseEarth",
        ComponentKind.PowerThreePhaseNeutral => "Comp_PowerThreePhaseNeutral",
        ComponentKind.PowerThreePhaseNeutralEarth => "Comp_PowerThreePhaseNeutralEarth",
        ComponentKind.PowerPlus => "Comp_PowerPlus",
        ComponentKind.PowerMinus => "Comp_PowerMinus",
        ComponentKind.PowerDcEarth => "Comp_PowerDcEarth",
        _ => kind.ToString()
    });

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
        if (kind is ComponentKind.TimerOnContactNO or ComponentKind.TimerOnContactNC
            or ComponentKind.TimerOffContactNO or ComponentKind.TimerOffContactNC
            or ComponentKind.TimerOnOffContactNO_NC)
        {
            var timer = _project.Components.LastOrDefault(c =>
                c.Kind is ComponentKind.TimerOnDelay or ComponentKind.TimerOffDelay or ComponentKind.TimerOnOffDelay);
            return timer is not null ? timer.Tag : "KT1";
        }
        if (kind is ComponentKind.TimerOnDelay or ComponentKind.TimerOffDelay or ComponentKind.TimerOnOffDelay)
            return NextTimerTag();
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

    private string NextTimerTag()
{
    var used = _project.Components
        .Where(c => c.Kind is ComponentKind.TimerOnDelay or ComponentKind.TimerOffDelay or ComponentKind.TimerOnOffDelay)
        .Select(c => c.Tag)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    for (var i = 1; i < 1000; i++)
    {
        var candidate = $"KT{i}";
        if (!used.Contains(candidate)) return candidate;
    }
    return "KT" + Guid.NewGuid().ToString("N")[..4];
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
        StatusText.Text = L("Selected_Status", _selected.Tag, label);
    }

   
private void UpdateSimulationButtons()
{
    SimIndicatorDot.Fill = Brush(_simulator.IsRunning ? "#22C55E" : "#94A3B8");
    SimIndicatorText.Text = L(_simulator.IsRunning ? "Sim_Badge_Running" : "Sim_Badge_Stopped");
    RunButton.IsVisible = !_simulator.IsRunning;
    StopButton.IsVisible = _simulator.IsRunning;

    RunButton.IsEnabled = !_simulator.IsRunning;
    StopButton.IsEnabled = _simulator.IsRunning;

    StatusText.Text = _simulator.IsRunning
        ? _simulator.Message
        : L("Sim_Stopped");
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
        _timerSimTimer.Start();
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
        _timerSimTimer.Stop();
        _currentFlowTickCount = 0;
        StatusText.Text = L("Sim_Stopped_Edit_Ready");
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
    private static bool IsDarkTime(DateTime time) => time.Hour >= 19 || time.Hour < 7;

    private void SetTheme(bool dark)
    {
        var btnBg = dark ? Brush("#2A2F3A") : Brushes.White;
        var iconFg = dark ? Brush("#E2E8F0") : Brush("#010101");

        Resources["ToolBtnBg"] = btnBg;
        Resources["ToolBtnHoverBg"] = dark ? Brush("#3B4252") : Brush("#EFF6FF");
        Resources["ToolBtnBorder"] = dark ? Brush("#444C56") : Brush("#CBD5E1");
        Resources["ToolBtnHoverBorder"] = Brush("#60A5FA");

        LeftToolbarBorder.Background = dark ? Brush("#171717") : Brushes.White;
        LeftToolbarBorder.BorderBrush = dark ? Brush("#404040") : Brush("#D6DEE9");
        BackToSubcategoriesButton.Foreground = dark ? Brush("#E2E8F0") : Brush("#334155");

        foreach (var btn in SubcategoryPanel.Children.OfType<Button>())
            ApplyIconForeground(btn.Content, iconFg, btnBg);

        foreach (var btn in PalettePanel.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Button>()))
            ApplyIconForeground(btn.Content, iconFg, btnBg);
        var trayBg = dark ? Brush("#262626") : Brush("#F1F5F9");
        SubcategoryRow.Background = trayBg;
        PaletteRow.Background = trayBg;
        _darkTheme = dark;
        if (Application.Current is { } app) app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (_activeCategory is not null) FilterPalette(_activeCategory);
        Background = dark ? Brushes.Black : Brush("#F3F6FA");
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
        UpdateCategoryButtonsAppearance();
        SubcategoryRow.BorderBrush = dark ? Brush("#404040") : Brush("#E2E8F0");
        ViewportBorder.Background = dark ? Brushes.Black : Brushes.White;
        ViewportBorder.BorderBrush = dark ? Brush("#262626") : Brush("#D6DEE9");
        WorkAreaBadge.Background = dark ? Brush("#1E293B") : Brush("#F1F5F9");
        WorkAreaLabel.Foreground = dark ? Brush("#CBD5E1") : Brush("#64748B");
        StatusBar.Background = dark ? Brush("#171717") : Brushes.White;
        StatusBar.BorderBrush = dark ? Brush("#404040") : Brush("#D9E1EC");
        StatusText.Foreground = dark ? Brush("#CBD5E1") : Brush("#64748B");
        SchematicCanvas.Background = dark ? Brushes.Black : Brushes.White;
        Viewport.Background = dark ? Brushes.Black : Brushes.White;
        var toolFg = dark ? Brush("#E2E8F0") : Brush("#334155");
        UndoToolbarButton.Foreground = toolFg;
        RotateToolbarButton.Foreground = toolFg;
        DeleteToolbarButton.Foreground = toolFg;
        NewTabButton.Foreground = dark ? Brush("#CBD5E1") : Brush("#64748B");
        if (this.FindControl<Border>("TabsBarBorder") is { } tabsBar)
        {
            tabsBar.Background = dark ? Brush("#1A1A1A") : Brush("#F8FAFC");
            tabsBar.BorderBrush = dark ? Brush("#333333") : Brush("#E2E8F0");
        }
        RebuildTabs();
        _gridCount = -1;
        Redraw();
    }

    private void ThemeToggle_Click(object? sender, RoutedEventArgs e) => SetTheme(!_darkTheme);

    private static string L(string key, params object[] args) => LocalizationService.Get(key, args);

    private static string LocalizedPaletteTitle(string title)
    {
        var key = title switch
        {
            "Звонок" => "Comp_Bell",
            "Сирена" => "Comp_Siren",
            "Предохранитель" => "Comp_Fuse",
            "Контакт таймера вкл. NO" => "Comp_TimerOnContactNO",
            "Контакт таймера вкл. NC" => "Comp_TimerOnContactNC",
            "Контакт таймера выкл. NO" => "Comp_TimerOffContactNO",
            "Контакт таймера выкл. NC" => "Comp_TimerOffContactNC",
            "Контакт таймера вкл/выкл" => "Comp_TimerOnOffContactNO_NC",
            "Таймер задержки включения" => "Comp_TimerOnDelay",
            "Таймер задержки выключения" => "Comp_TimerOffDelay",
            "Таймер вкл/выкл" => "Comp_TimerOnOffDelay",
            "Crossover switch" => "Comp_CrossoverSwitch",
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
        LanguageButton.Content = LocalizationService.CurrentLanguage switch
        {
            AppLanguage.Ru => "RU",
            AppLanguage.En => "EN",
            _ => "ES"
        };
        ToolTip.SetTip(ThemeToggleButton, L("Menu_Theme"));
        ToolTip.SetTip(UndoToolbarButton, L("Tool_Undo"));
        ToolTip.SetTip(RotateToolbarButton, L("Tool_Rotate"));
        ToolTip.SetTip(DeleteToolbarButton, L("Tool_Delete"));
        ToolTip.SetTip(WireColorToolbarButton, L("Tool_WireColor"));
        ToolTip.SetTip(NewTabButton, L("Tab_New"));
        SimIndicatorText.Text = L(_simulator.IsRunning ? "Sim_Badge_Running" : "Sim_Badge_Stopped");
        ToolTip.SetTip(LanguageButton, L("Menu_Language"));

        var categoryKeys = new[] { "Cat_Power", "Cat_Breakers", "Cat_Contactors", "Cat_Motors", "Cat_Buttons", "Cat_Coils", "Cat_Relays" };
        var categories = CategoryPanel.Children.OfType<Button>().ToArray();
        foreach (var btn in SubcategoryPanel.Children.OfType<Button>())
        if (btn.Tag is string subKey && Subcategories.FirstOrDefault(s => s.Key == subKey) is { } sub)
            ToolTip.SetTip(btn, L(sub.LocKey));
        for (var i = 0; i < Math.Min(categoryKeys.Length, categories.Length); i++)
            ToolTip.SetTip(categories[i], L(categoryKeys[i]));
        foreach (var button in PalettePanel.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Button>()))
            if (button.Tag is string title) ToolTip.SetTip(button, LocalizedPaletteTitle(title));

        RunButton.Content = $"▶ {L("Run")}";
        StopButton.Content = $"■ {L("Stop")}";
        WorkAreaLabel.Text = L("Work_Area");

        if (_simulator.IsRunning)
        {
            _simulator.RefreshLocalization(_project);
            if (_selected is not null)
                StatusText.Text = $"{_selected.Tag}: {_simulator.GetState(_selected.Id)}";
            else
                StatusText.Text = _simulator.Message;
        }
        else if (_selected is not null)
        {
            RefreshSelectedStatus();
        }
        else
        {
            StatusText.Text = _showingZoomStatus
                ? $"{L("Zoom_Status")} {Math.Round(_zoom * 100)}%."
                : L("Ready_Status");
        }
    }

    private bool _showingZoomStatus = false;

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        await SaveProjectAsync();
    }

    private async Task<bool> SaveProjectAsync()
    {
        var path = _currentFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L("Save_Dialog_Title"),
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
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (string.IsNullOrEmpty(System.IO.Path.GetExtension(path))) path += ".cforge";
        }
        _files.Save(path, _project);
        _currentFilePath = path;
        if (_activeTab is not null)
        {
            _activeTab.FilePath = path;
            _activeTab.IsDirty = false;
        }
        RebuildTabs();  
        SaveCameraState();
        StatusText.Text = L("Saved_Status", System.IO.Path.GetFileName(path));
        return true;
    }
    private void CenterProjectOnCanvas(ProjectDocument doc)
    {
        if (doc.Components.Count == 0) return;

        var minX = doc.Components.Min(c => c.X);
        var maxX = doc.Components.Max(c => c.X);
        var minY = doc.Components.Min(c => c.Y);
        var maxY = doc.Components.Max(c => c.Y);

        var canvasWidth = double.IsNaN(SchematicCanvas.Width) ? 3000.0 : SchematicCanvas.Width;
        var canvasHeight = double.IsNaN(SchematicCanvas.Height) ? 2000.0 : SchematicCanvas.Height;

        var dx = Snap(canvasWidth / 2.0 - (minX + maxX) / 2.0);
        var dy = Snap(canvasHeight / 2.0 - (minY + maxY) / 2.0);

        foreach (var c in doc.Components)
        {
            c.X += dx;
            c.Y += dy;
        }

        foreach (var w in doc.Wires)
        {
            if (w.Waypoints is null) continue;
            foreach (var wp in w.Waypoints)
            {
                wp.X += dx;
                wp.Y += dy;
            }
        }
    }

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (_simulator.IsRunning) { StatusText.Text = L("Sim_Stop_Before_Open"); return; }
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L("Open_Dialog_Title"),
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
            var fullPath = System.IO.Path.GetFullPath(fileName);

            // Если этот файл уже открыт в одной из вкладок — просто переключаемся на неё
            if (!isCadSimu)
            {
                var existing = _tabs.FirstOrDefault(t =>
                    !string.IsNullOrWhiteSpace(t.FilePath) &&
                    string.Equals(System.IO.Path.GetFullPath(t.FilePath), fullPath, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    SwitchToTab(existing);
                    return;
                }
            }

            ProjectDocument loadedProject;
            string[] unsupportedCadParts = Array.Empty<string>();
            if (isCadSimu)
            {
                var imported = _cadImporter.Import(fileName);
                loadedProject = imported.Project;
                CenterProjectOnCanvas(loadedProject);
                unsupportedCadParts = imported.UnsupportedComponents.ToArray();
            }
            else loadedProject = _files.Load(fileName);

            foreach (var wire in loadedProject.Wires)
                if (string.IsNullOrWhiteSpace(wire.ColorKey) || !WireColors.Any(c => c.Key == wire.ColorKey)) wire.ColorKey = "Brown";

            var openedPath = isCadSimu ? null : fileName;

            // Если сейчас открыта ровно одна пустая вкладка "Untitled" без действий — замещаем её.
            // Иначе — открываем файл в новой вкладке справа.
            if (_tabs.Count == 1 && _activeTab is { FilePath: null } singleTab &&
                _project.Components.Count == 0 && _project.Wires.Count == 0 && _undoStack.Count == 0)
            {
                singleTab.Project = loadedProject;
                singleTab.FilePath = openedPath;
                singleTab.CameraInitialized = false;
                _activeTab = null;
                SwitchToTab(singleTab);
            }
            else
            {
                var newTab = new ProjectTab
                {
                    Project = loadedProject,
                    FilePath = openedPath
                };
                _tabs.Add(newTab);
                SwitchToTab(newTab);
            }

            string fileNameOnly = System.IO.Path.GetFileName(fileName);
            StatusText.Text = isCadSimu
                ? $"{LocalizationService.Get("ImportedFromCadSimu")}: {fileNameOnly}"
                : $"{LocalizationService.Get("Opened")}: {fileNameOnly}";

            if (unsupportedCadParts.Length > 0)
            {
                var listed = string.Join(Environment.NewLine, unsupportedCadParts.Take(12));
                var suffix = unsupportedCadParts.Length > 12 ? L("Unsupported_More", unsupportedCadParts.Length - 12) : "";
                await ShowMessageAsync(L("Unsupported_Title"), L("Unsupported_Body", listed) + suffix);
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(L("Open_Error_Title"), L("Open_Error_Body", ex.Message));
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
