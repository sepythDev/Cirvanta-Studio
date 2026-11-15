using System.Numerics;
using CircuitForge.Models;
using CircuitForge.Services;
namespace CircuitForge.Simulation;

/// <summary>
/// Resistive nodal solver with steady-state AC phasors for control schematics.
/// It models RMS voltage and 120-degree phase offsets, but not waveform transients, inductance,
/// motor dynamics or certified protective-device time/current curves. This is not safety-certified.
/// </summary>


public sealed class CircuitSimulator
{
    private const double OpenResistance = 1e12;
    private const double ClosedContactResistance = 0.01;
    private readonly Dictionary<Guid, bool> _pressedButtons = new();
    private readonly Dictionary<Guid, bool> _switchClosed = new();
    private readonly Dictionary<Guid, bool> _protectiveClosed = new();
    private readonly Dictionary<Guid, bool> _thermalRelayTripped = new();
    private readonly Dictionary<Guid, bool> _overloadTripped = new();
    private readonly Dictionary<Guid, bool> _sourceEnabled = new();
    private readonly Dictionary<Guid, bool> _coilEnergized = new();
    private readonly HashSet<Guid> _blownFuses = new();

    public bool IsFuseBlown(Guid id) => _blownFuses.Contains(id);

    public void ToggleFuse(Guid id, ProjectDocument project)
    {
        if (_blownFuses.Contains(id))
            _blownFuses.Remove(id);
        else
            _blownFuses.Add(id);

        Solve(project);
    }
    private sealed class TimerRuntimeState
    {
        public bool CoilPowered;
        public bool OutputActive;
        public DateTime LastTickUtc;
        public double ElapsedSeconds;
    }
    private readonly Dictionary<Guid, TimerRuntimeState> _timerStates = new();
    private readonly Dictionary<Guid, bool> _motorClockwise = new();
    private readonly Dictionary<Guid, HashSet<int>> _conductingContactorPoles = new();
    private readonly Dictionary<Guid, bool> _componentEnergized = new();
    private readonly Dictionary<Guid, string> _states = new();
    private readonly Dictionary<Guid, bool> _auxClosed = new();
    private readonly Dictionary<Guid, bool> _conSwitchedToNo = new();
    private readonly Dictionary<string, double> _terminalVoltages = new();
    private readonly HashSet<Guid> _energizedWires = new();
    private readonly HashSet<Guid> _currentWires = new();
    private readonly Dictionary<Guid, bool> _currentWireDirections = new();
    private readonly List<List<List<(Guid WireId, bool Forward)>>> _currentFlowSequences = new();
    private int _currentFlowSequenceIndex;
    private List<(TerminalNode A, TerminalNode B)> _conductingPairs = new();

    public bool IsRunning { get; private set; }
    public bool HasShortCircuit { get; private set; }
    private readonly List<(double X, double Y)> _shortCircuitPoints = new();
    public IReadOnlyList<(double X, double Y)> ShortCircuitPoints => _shortCircuitPoints;
    public string Message { get; private set; } = LocalizationService.Get("Sim_Stopped");

    private static bool IsTimer(ComponentKind kind) =>
        kind is ComponentKind.TimerOnDelay or ComponentKind.TimerOffDelay or ComponentKind.TimerOnOffDelay;

    public bool IsTimerOutputActive(Guid id) =>
        _timerStates.TryGetValue(id, out var st) && st.OutputActive;

    public double GetTimerRemainingSeconds(ComponentModel timer)
    {
        if (!_timerStates.TryGetValue(timer.Id, out var st)) return 0;
        if (timer.Kind == ComponentKind.TimerOnDelay && st.CoilPowered && !st.OutputActive)
            return Math.Max(0, timer.DelayOnSeconds - st.ElapsedSeconds);
        if (timer.Kind == ComponentKind.TimerOffDelay && !st.CoilPowered && st.OutputActive)
            return Math.Max(0, timer.DelayOffSeconds - st.ElapsedSeconds);
        if (timer.Kind == ComponentKind.TimerOnOffDelay)
        {
            if (timer.EnableTon && st.CoilPowered && !st.OutputActive)
                return Math.Max(0, timer.DelayOnSeconds - st.ElapsedSeconds);
            if (timer.EnableTof && !st.CoilPowered && st.OutputActive)
                return Math.Max(0, timer.DelayOffSeconds - st.ElapsedSeconds);
        }
        return 0;
    }

    public void Start(ProjectDocument project)
    {
        _blownFuses.Clear();
        _pressedButtons.Clear();
        _switchClosed.Clear();
        _protectiveClosed.Clear();
        _thermalRelayTripped.Clear();
        _overloadTripped.Clear();
        _sourceEnabled.Clear();
        _coilEnergized.Clear();
        _timerStates.Clear();
        _auxClosed.Clear();
        _conSwitchedToNo.Clear();
        _motorClockwise.Clear();
        _conductingContactorPoles.Clear();
        _componentEnergized.Clear();
        _states.Clear();
        _terminalVoltages.Clear();
        _energizedWires.Clear();
        _currentWires.Clear();
        _currentWireDirections.Clear();
        _currentFlowSequences.Clear();
        _currentFlowSequenceIndex = 0;
        _conductingPairs.Clear();
        _shortCircuitPoints.Clear();
        foreach (var c in project.Components)
        {
            if (IsButton(c.Kind)) _pressedButtons[c.Id] = false;
            if (IsContactSwitch(c.Kind) || c.Kind is ComponentKind.LimitSwitchNO or ComponentKind.LimitSwitchNC) 
                _switchClosed[c.Id] = c.Kind is ComponentKind.ToggleSwitchNC or ComponentKind.LimitSwitchNC;
            if (IsProtective(c.Kind)) _protectiveClosed[c.Id] = c.Kind == ComponentKind.ThermalRelay;
            if (c.Kind == ComponentKind.ThermalRelay) _thermalRelayTripped[c.Id] = false;
            if (c.Kind is ComponentKind.OverloadNC or ComponentKind.OverloadNO) _overloadTripped[c.Id] = false;
            if (IsSource(c.Kind)) _sourceEnabled[c.Id] = true;
            if (c.Kind == ComponentKind.Coil) _coilEnergized[c.Id] = false;
            if (IsTimer(c.Kind)) _timerStates[c.Id] = new TimerRuntimeState { LastTickUtc = DateTime.UtcNow };
            _componentEnergized[c.Id] = false;
            _states[c.Id] = LocalizationService.Get("Sim_State_Off");
        }
        IsRunning = true;
        Solve(project);
    }

    public void Stop()
    {
        IsRunning = false;
        _pressedButtons.Clear();
        _timerStates.Clear();
        _auxClosed.Clear();
        _conSwitchedToNo.Clear();
        _energizedWires.Clear();
        _currentWires.Clear();
        _currentWireDirections.Clear();
        _currentFlowSequences.Clear();
        _currentFlowSequenceIndex = 0;
        _conductingPairs.Clear();
        _terminalVoltages.Clear();
        HasShortCircuit = false;
        _shortCircuitPoints.Clear();
        foreach (var id in _componentEnergized.Keys.ToArray()) _componentEnergized[id] = false;
        foreach (var id in _coilEnergized.Keys.ToArray()) _coilEnergized[id] = false;
        foreach (var id in _states.Keys.ToArray()) _states[id] = LocalizationService.Get("Sim_State_Stopped");
        Message = LocalizationService.Get("Sim_Stopped");
    }

    public bool IsEnergized(Guid id) => _componentEnergized.TryGetValue(id, out var v) && v;
    public bool IsThermalRelayTripped(Guid id) => _thermalRelayTripped.GetValueOrDefault(id);
    public bool IsContactorClosed(Guid id, ProjectDocument project)
    {
        var contactor = project.Components.FirstOrDefault(c => c.Id == id &&
            c.Kind is ComponentKind.Contactor2P or ComponentKind.Contactor3P or ComponentKind.Contactor4P);
        return IsRunning && contactor is not null && project.Components.Any(c =>
            c.Kind == ComponentKind.Coil && TagMatches(c, contactor.Tag) && _coilEnergized.GetValueOrDefault(c.Id));
    }
    public bool IsWireEnergized(Guid id) => _energizedWires.Contains(id);
    public bool IsWireCarryingCurrent(Guid id) => _currentWires.Contains(id);
    public bool IsCurrentFlowForward(Guid id) => _currentWireDirections.GetValueOrDefault(id, true);
    public void AdvanceCurrentFlowSequence()
    {
        if (_currentFlowSequences.Count < 2) return;
        _currentFlowSequenceIndex = (_currentFlowSequenceIndex + 1) % _currentFlowSequences.Count;
        RefreshCurrentFlowWires();
    }
    public bool IsMotorClockwise(Guid id) => _motorClockwise.GetValueOrDefault(id, true);
    public bool IsContactorPoleConducting(Guid id, int pole) =>
        _conductingContactorPoles.TryGetValue(id, out var poles) && poles.Contains(pole);
    public string GetState(Guid id) => _states.TryGetValue(id, out var s) ? s : "";
    public bool GetContactClosed(Guid id, ComponentKind kind) => kind switch
    {
        ComponentKind.Fuse => !_blownFuses.Contains(id),
        ComponentKind.LimitSwitchNO => _switchClosed.GetValueOrDefault(id),
        ComponentKind.LimitSwitchNC => _switchClosed.GetValueOrDefault(id),
        ComponentKind.PushButtonNO => _pressedButtons.GetValueOrDefault(id),
        ComponentKind.PushButtonNC => !_pressedButtons.GetValueOrDefault(id),
        ComponentKind.PushButtonNO_NC => _pressedButtons.GetValueOrDefault(id),
        ComponentKind.PushButtonCon => _pressedButtons.GetValueOrDefault(id),
        ComponentKind.ToggleSwitchNO or ComponentKind.ToggleSwitchNC => _switchClosed.GetValueOrDefault(id),
        ComponentKind.ToggleSwitchNO_NC or ComponentKind.ToggleSwitchCon or ComponentKind.CrossoverSwitch => _switchClosed.GetValueOrDefault(id),
        ComponentKind.AuxContactNO or ComponentKind.AuxContactNC or ComponentKind.AuxContactNO_NC or ComponentKind.OverloadNO or ComponentKind.ThermalRelayAuxNC or ComponentKind.ThermalRelayAuxNO_NC => _auxClosed.GetValueOrDefault(id),
        ComponentKind.AuxContactNO or ComponentKind.AuxContactNC or ComponentKind.AuxContactNO_NC
            or ComponentKind.TimerOnContactNO or ComponentKind.TimerOnContactNC
            or ComponentKind.TimerOffContactNO or ComponentKind.TimerOffContactNC
            or ComponentKind.TimerOnOffContactNO_NC
            or ComponentKind.OverloadNO or ComponentKind.ThermalRelayAuxNC or ComponentKind.ThermalRelayAuxNO_NC => _auxClosed.GetValueOrDefault(id),
        ComponentKind.ConContact => _conSwitchedToNo.GetValueOrDefault(id),
        ComponentKind.OverloadNC => !_overloadTripped.GetValueOrDefault(id),
        ComponentKind.CircuitBreaker or ComponentKind.DifferentialBreaker or ComponentKind.Breaker1P or ComponentKind.Breaker1PN or ComponentKind.Breaker2P or ComponentKind.Breaker3P or ComponentKind.Breaker3PN or ComponentKind.Rcd1PN or ComponentKind.Rcd3PN or ComponentKind.ThermalRelay => _protectiveClosed.GetValueOrDefault(id),
        _ => false
    };
    public double GetTerminalVoltage(Guid componentId, string terminalId) => _terminalVoltages.GetValueOrDefault(VoltageKey(componentId, terminalId));

    public void SetButton(Guid id, bool pressed, ProjectDocument project)
    {
        if (!IsRunning || !_pressedButtons.ContainsKey(id)) return;
        _pressedButtons[id] = pressed;
        Solve(project);
    }

    public void SetButtonsByTag(Guid id, bool pressed, ProjectDocument project)
    {
        if (!IsRunning) return;
        var source = project.Components.FirstOrDefault(c => c.Id == id && IsButton(c.Kind));
        if (source is null) return;
        foreach (var c in project.Components.Where(c => IsButton(c.Kind) && TagMatches(c, source.Tag)))
            _pressedButtons[c.Id] = pressed;
        Solve(project);
    }

    public void ToggleContactSwitch(Guid id, ProjectDocument project)
    {
        if (!IsRunning || !_switchClosed.ContainsKey(id)) return;
        _switchClosed[id] = !_switchClosed[id];
        Solve(project);
    }

    public void ToggleProtective(Guid id, ProjectDocument project)
    {
        if (!IsRunning || !_protectiveClosed.ContainsKey(id)) return;
        _protectiveClosed[id] = !_protectiveClosed[id];
        Solve(project);
    }

    public void ToggleProtectiveGroup(Guid id, ProjectDocument project)
    {
        if (!IsRunning || !_protectiveClosed.ContainsKey(id)) return;
        var source = project.Components.FirstOrDefault(c => c.Id == id);
        if (source is null) return;
        var newState = !_protectiveClosed[id];
        foreach (var c in project.Components.Where(c => IsProtective(c.Kind) && TagMatches(c, source.Tag)))
            _protectiveClosed[c.Id] = newState;
        Solve(project);
    }

    public void ToggleThermalRelayGroup(Guid id, ProjectDocument project)
    {
        if (!IsRunning || !_thermalRelayTripped.ContainsKey(id)) return;
        var source = project.Components.FirstOrDefault(c => c.Id == id && c.Kind == ComponentKind.ThermalRelay);
        if (source is null) return;

        var tripped = !_thermalRelayTripped[id];
        foreach (var relay in project.Components.Where(c => c.Kind == ComponentKind.ThermalRelay && TagMatches(c, source.Tag)))
        {
            _thermalRelayTripped[relay.Id] = tripped;
            _protectiveClosed[relay.Id] = !tripped;
        }
        Solve(project);
    }

    public void ToggleOverload(Guid id, ProjectDocument project)
    {
        if (!IsRunning || !_overloadTripped.ContainsKey(id)) return;
        _overloadTripped[id] = !_overloadTripped[id];
        Solve(project);
    }

    public void ToggleOverloadGroup(Guid id, ProjectDocument project)
    {
        if (!IsRunning || !_overloadTripped.ContainsKey(id)) return;
        var source = project.Components.FirstOrDefault(c => c.Id == id);
        if (source is null) return;
        var tripped = !_overloadTripped[id];
        foreach (var c in project.Components.Where(c => (c.Kind is ComponentKind.OverloadNC or ComponentKind.OverloadNO) && TagMatches(c, source.Tag)))
            _overloadTripped[c.Id] = tripped;
        Solve(project);
    }

    private static bool TagMatches(ComponentModel component, string tag) =>
        string.Equals(component.Tag.Trim(), tag.Trim(), StringComparison.OrdinalIgnoreCase);

    public void ToggleSource(Guid id, ProjectDocument project)
    {
        if (!IsRunning || !_sourceEnabled.ContainsKey(id)) return;
        _sourceEnabled[id] = !_sourceEnabled[id];
        Solve(project);
    }

    public bool Step(ProjectDocument project)
    {
        if (!IsRunning || _timerStates.Count == 0) return false;

        var now = DateTime.UtcNow;
        var outputChanged = false;
        var counting = false;

        foreach (var timer in project.Components.Where(c => IsTimer(c.Kind)))
        {
            if (!_timerStates.TryGetValue(timer.Id, out var st))
            {
                st = new TimerRuntimeState { LastTickUtc = now };
                _timerStates[timer.Id] = st;
            }

            var dt = Math.Max(0, (now - st.LastTickUtc).TotalSeconds);
            st.LastTickUtc = now;

            var delayOn = timer.Kind == ComponentKind.TimerOnOffDelay && !timer.EnableTon ? 0.0 : Math.Max(0, timer.DelayOnSeconds);
            var delayOff = timer.Kind == ComponentKind.TimerOnOffDelay && !timer.EnableTof ? 0.0 : Math.Max(0, timer.DelayOffSeconds);

            switch (timer.Kind)
            {
                case ComponentKind.TimerOnDelay:
                    if (st.CoilPowered)
                    {
                        if (!st.OutputActive)
                        {
                            st.ElapsedSeconds += dt;
                            counting = true;
                            if (st.ElapsedSeconds >= delayOn)
                            {
                                st.OutputActive = true;
                                st.ElapsedSeconds = 0;
                                outputChanged = true;
                            }
                        }
                    }
                    else
                    {
                        if (st.OutputActive) outputChanged = true;
                        st.OutputActive = false;
                        st.ElapsedSeconds = 0;
                    }
                    break;

                case ComponentKind.TimerOffDelay:
                    if (st.CoilPowered)
                    {
                        if (!st.OutputActive) outputChanged = true;
                        st.OutputActive = true;
                        st.ElapsedSeconds = 0;
                    }
                    else if (st.OutputActive)
                    {
                        st.ElapsedSeconds += dt;
                        counting = true;
                        if (st.ElapsedSeconds >= delayOff)
                        {
                            st.OutputActive = false;
                            st.ElapsedSeconds = 0;
                            outputChanged = true;
                        }
                    }
                    break;

                case ComponentKind.TimerOnOffDelay:
                    if (st.CoilPowered)
                    {
                        if (!st.OutputActive)
                        {
                            if (delayOn <= 0)
                            {
                                st.OutputActive = true;
                                st.ElapsedSeconds = 0;
                                outputChanged = true;
                            }
                            else
                            {
                                st.ElapsedSeconds += dt;
                                counting = true;
                                if (st.ElapsedSeconds >= delayOn)
                                {
                                    st.OutputActive = true;
                                    st.ElapsedSeconds = 0;
                                    outputChanged = true;
                                }
                            }
                        }
                        else st.ElapsedSeconds = 0;
                    }
                    else
                    {
                        if (st.OutputActive)
                        {
                            if (delayOff <= 0)
                            {
                                st.OutputActive = false;
                                st.ElapsedSeconds = 0;
                                outputChanged = true;
                            }
                            else
                            {
                                st.ElapsedSeconds += dt;
                                counting = true;
                                if (st.ElapsedSeconds >= delayOff)
                                {
                                    st.OutputActive = false;
                                    st.ElapsedSeconds = 0;
                                    outputChanged = true;
                                }
                            }
                        }
                        else st.ElapsedSeconds = 0;
                    }
                    break;
            }
        }

        if (outputChanged)
        {
            Solve(project);
            return true;
        }

        if (counting)
        {
            UpdateStatuses(project);
            return true;
        }

        return false;
    }

    public void RefreshLocalization(ProjectDocument project)
    {
        if (!IsRunning)
        {
            Message = LocalizationService.Get("Sim_Stopped");
            foreach (var id in _states.Keys.ToArray())
                _states[id] = LocalizationService.Get("Sim_State_Stopped");
            return;
        }

        UpdateStatuses(project);

        if (!project.Components.Any(c => IsSource(c.Kind) && _sourceEnabled.GetValueOrDefault(c.Id)) && GetPassiveMarkerSupplies(project).Count == 0)
        {
            Message = LocalizationService.Get("Sim_No_Active_Source");
            return;
        }

        if (HasShortCircuit)
        {
            Message = LocalizationService.Get("Sim_Short_Circuit_Warning");
            return;
        }

        var activeLoads = project.Components.Count(c =>
            (c.Kind is ComponentKind.Coil or ComponentKind.TimerOnDelay or ComponentKind.TimerOffDelay or ComponentKind.TimerOnOffDelay
                or ComponentKind.Lamp or ComponentKind.BlinkingLamp or ComponentKind.Bell or ComponentKind.Siren
                or ComponentKind.Motor or ComponentKind.DcMotor or ComponentKind.AcMotor or ComponentKind.AcMotor1P) &&
            IsEnergized(c.Id));

        Message = activeLoads > 0
            ? LocalizationService.Get("Sim_Active_Loads", activeLoads)
            : LocalizationService.Get("Sim_Source_On_No_Loads");
    }

    private void Solve(ProjectDocument project)
    {
        HasShortCircuit = false;
        _energizedWires.Clear();
        _currentWires.Clear();
        _conductingContactorPoles.Clear();
        _terminalVoltages.Clear();

        if (!project.Components.Any(c => IsSource(c.Kind) && _sourceEnabled.GetValueOrDefault(c.Id)) && GetPassiveMarkerSupplies(project).Count == 0)
        {
            foreach (var id in _componentEnergized.Keys.ToArray()) _componentEnergized[id] = false;
            foreach (var id in _coilEnergized.Keys.ToArray()) _coilEnergized[id] = false;
            _motorClockwise.Clear();
            _conductingContactorPoles.Clear();
            UpdateStatuses(project);
            Message = LocalizationService.Get("Sim_No_Active_Source");
            return;
        }

        // Detect a hard wire-only short before solving. It would otherwise make an ideal voltage-source matrix inconsistent.
        var rawUnion = BuildWireUnion(project);
        var activeSourceShort = false;
        foreach (var source in project.Components.Where(c => IsSource(c.Kind) && _sourceEnabled.GetValueOrDefault(c.Id)))
        {
            var liveTerminals = GetLiveTerminalIds(source).ToList();
            var liveRoots = liveTerminals.Select(t => rawUnion.Find(new TerminalNode(source.Id, t))).ToList();
            var neutralRoot = rawUnion.Find(new TerminalNode(source.Id, "R"));
            if (liveRoots.Any(root => root == neutralRoot) || liveRoots.Distinct().Count() != liveRoots.Count)
            {
                HasShortCircuit = true;
                activeSourceShort = true;
                var shortPair = liveTerminals.Select((terminal, index) => (terminal, root: liveRoots[index]))
                    .FirstOrDefault(item => item.root == neutralRoot);
                if (shortPair.terminal is not null)
                    AddShortCircuitPoint(FindWirePathMidpoint(project, new TerminalNode(source.Id, shortPair.terminal), new TerminalNode(source.Id, "R")));
                else if (liveTerminals.Count > 1)
                    AddShortCircuitPoint(FindWirePathMidpoint(project, new TerminalNode(source.Id, liveTerminals[0]), new TerminalNode(source.Id, liveTerminals[1])));
            }
        }
        if (activeSourceShort)
        {
            foreach (var id in _componentEnergized.Keys.ToArray()) _componentEnergized[id] = false;
            foreach (var id in _coilEnergized.Keys.ToArray()) _coilEnergized[id] = false;
            UpdateStatuses(project);
            Message = LocalizationService.Get("Sim_Short_Circuit_Source");
            return;
        }

        GetHealthyPassiveMarkerSupplies(project, rawUnion);

        SolveResult? result = null;
        // Relay contacts are tied to coil state; iterate to a stable state (including self-hold circuits).
        var maxIterations = Math.Max(4, project.Components.Count + 2);
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            result = SolveNodal(project);
            if (result is null)
            {
                Message = HasShortCircuit ? LocalizationService.Get("Sim_Short_Circuit_No_Breaker") : LocalizationService.Get("Sim_Solve_Check_Sources");
                foreach (var id in _componentEnergized.Keys.ToArray()) _componentEnergized[id] = false;
                foreach (var id in _coilEnergized.Keys.ToArray()) _coilEnergized[id] = false;
                UpdateStatuses(project);
                return;
            }

            var changed = false;
            foreach (var coil in project.Components.Where(c => c.Kind == ComponentKind.Coil))
            {
                var active = IsLoadPowered(coil, result, project);
                if (_coilEnergized.GetValueOrDefault(coil.Id) != active)
                {
                    _coilEnergized[coil.Id] = active;
                    changed = true;
                }
            }
            foreach (var timer in project.Components.Where(c => IsTimer(c.Kind)))
            {
                var powered = IsLoadPowered(timer, result, project);
                if (!_timerStates.TryGetValue(timer.Id, out var st))
                    _timerStates[timer.Id] = st = new TimerRuntimeState { LastTickUtc = DateTime.UtcNow };

                var delayOn = timer.Kind == ComponentKind.TimerOnOffDelay && !timer.EnableTon ? 0.0 : Math.Max(0, timer.DelayOnSeconds);
                var delayOff = timer.Kind == ComponentKind.TimerOnOffDelay && !timer.EnableTof ? 0.0 : Math.Max(0, timer.DelayOffSeconds);

                if (st.CoilPowered != powered)
                {
                    st.CoilPowered = powered;
                    st.ElapsedSeconds = 0;
                    st.LastTickUtc = DateTime.UtcNow;

                    if (timer.Kind == ComponentKind.TimerOnDelay && !powered && st.OutputActive)
                    {
                        st.OutputActive = false;
                        changed = true;
                    }
                    else if (timer.Kind == ComponentKind.TimerOnDelay && powered && delayOn <= 0 && !st.OutputActive)
                    {
                        st.OutputActive = true;
                        changed = true;
                    }
                    else if (timer.Kind == ComponentKind.TimerOffDelay && powered && !st.OutputActive)
                    {
                        st.OutputActive = true;
                        changed = true;
                    }
                    else if (timer.Kind == ComponentKind.TimerOffDelay && !powered && delayOff <= 0 && st.OutputActive)
                    {
                        st.OutputActive = false;
                        changed = true;
                    }
                    else if (timer.Kind == ComponentKind.TimerOnOffDelay && powered && delayOn <= 0 && !st.OutputActive)
                    {
                        st.OutputActive = true;
                        changed = true;
                    }
                    else if (timer.Kind == ComponentKind.TimerOnOffDelay && !powered && delayOff <= 0 && st.OutputActive)
                    {
                        st.OutputActive = false;
                        changed = true;
                    }
                }
            }
            if (!changed) break;
        }

        result = SolveNodal(project);
        if (result is null)
        {
            foreach (var id in _componentEnergized.Keys.ToArray()) _componentEnergized[id] = false;
            foreach (var id in _coilEnergized.Keys.ToArray()) _coilEnergized[id] = false;
            UpdateStatuses(project);
            Message = HasShortCircuit ? LocalizationService.Get("Sim_Short_Circuit_Warning") : LocalizationService.Get("Sim_Solve_Error");
            return;
        }

        foreach (var pair in result.TerminalVoltages) _terminalVoltages[VoltageKey(pair.Key.ComponentId, pair.Key.TerminalId)] = Complex.Abs(pair.Value);
        foreach (var motor in project.Components.Where(c => c.Kind == ComponentKind.AcMotor))
        {
            var a = result.TerminalVoltages.GetValueOrDefault(new TerminalNode(motor.Id, "L1"));
            var b = result.TerminalVoltages.GetValueOrDefault(new TerminalNode(motor.Id, "L2"));
            if (IsLoadPowered(motor, result, project))
                _motorClockwise[motor.Id] = (b * Complex.Conjugate(a)).Imaginary < 0;
        }
        _conductingContactorPoles.Clear();
        foreach (var contactor in project.Components.Where(c => c.Kind is ComponentKind.Contactor2P or ComponentKind.Contactor3P or ComponentKind.Contactor4P))
        {
            if (!IsContactorClosed(contactor.Id, project)) continue;
            var poleCount = contactor.Kind == ComponentKind.Contactor2P ? 2 : contactor.Kind == ComponentKind.Contactor3P ? 3 : 4;
            var conducting = new HashSet<int>();
            for (var pole = 1; pole <= poleCount; pole++)
            {
                var a = result.TerminalVoltages.GetValueOrDefault(new TerminalNode(contactor.Id, $"P{pole}in"));
                var b = result.TerminalVoltages.GetValueOrDefault(new TerminalNode(contactor.Id, $"P{pole}out"));
                if (Complex.Abs(a - b) / ClosedContactResistance > 0.01) conducting.Add(pole - 1);
            }
            _conductingContactorPoles[contactor.Id] = conducting;
        }
        foreach (var c in project.Components)
        {
            _componentEnergized[c.Id] = c.Kind switch
            {
                ComponentKind.Coil => _coilEnergized.GetValueOrDefault(c.Id),
                ComponentKind.TimerOnDelay or ComponentKind.TimerOffDelay or ComponentKind.TimerOnOffDelay => _timerStates.TryGetValue(c.Id, out var ts) && (ts.CoilPowered || ts.OutputActive),
                ComponentKind.Lamp or ComponentKind.BlinkingLamp or ComponentKind.Bell or ComponentKind.Siren or ComponentKind.Motor or ComponentKind.DcMotor or ComponentKind.AcMotor or ComponentKind.AcMotor1P => IsLoadPowered(c, result, project),
                ComponentKind.PushButtonNO => _pressedButtons.GetValueOrDefault(c.Id),
                ComponentKind.PushButtonNC => !_pressedButtons.GetValueOrDefault(c.Id),
                ComponentKind.PushButtonNO_NC => _pressedButtons.GetValueOrDefault(c.Id),
                ComponentKind.PushButtonCon => _pressedButtons.GetValueOrDefault(c.Id),
                ComponentKind.ToggleSwitchNO or ComponentKind.ToggleSwitchNC => _switchClosed.GetValueOrDefault(c.Id),
                ComponentKind.ToggleSwitchNO_NC or ComponentKind.ToggleSwitchCon or ComponentKind.CrossoverSwitch => _switchClosed.GetValueOrDefault(c.Id),
                ComponentKind.CircuitBreaker or ComponentKind.DifferentialBreaker or ComponentKind.Breaker1P or ComponentKind.Breaker1PN or ComponentKind.Breaker2P or ComponentKind.Breaker3P or ComponentKind.Breaker3PN or ComponentKind.Rcd1PN or ComponentKind.Rcd3PN or ComponentKind.ThermalRelay or ComponentKind.OverloadNC or ComponentKind.OverloadNO or ComponentKind.ThermalRelayAuxNC or ComponentKind.ThermalRelayAuxNO_NC or ComponentKind.AuxContactNO or ComponentKind.AuxContactNC or ComponentKind.AuxContactNO_NC or ComponentKind.ConContact or ComponentKind.TimerOnContactNO or ComponentKind.TimerOnContactNC or ComponentKind.TimerOffContactNO or ComponentKind.TimerOffContactNC or ComponentKind.TimerOnOffContactNO_NC => HasLiveTerminalVoltage(c),
                _ when ComponentTerminals.IsPowerMarker(c.Kind) => HasLiveTerminalVoltage(c),
                ComponentKind.AcSupply or ComponentKind.Supply24V => _sourceEnabled.GetValueOrDefault(c.Id),
                _ => false
            };
        }

        foreach (var wire in project.Wires)
        {
            var va = _terminalVoltages.GetValueOrDefault(VoltageKey(wire.StartComponentId, wire.StartTerminalId));
            var vb = _terminalVoltages.GetValueOrDefault(VoltageKey(wire.EndComponentId, wire.EndTerminalId));
            // A wire is gently highlighted if it is on an appreciably non-zero RMS potential.
            if (Math.Max(Math.Abs(va), Math.Abs(vb)) > 8) _energizedWires.Add(wire.Id);
        }
        MarkCurrentWires(project);

        UpdateStatuses(project);
        if (HasShortCircuit)
        {
            Message = LocalizationService.Get("Sim_Short_Circuit_Warning");
            return;
        }
        var activeLoads = project.Components.Count(c => (c.Kind is ComponentKind.Coil or ComponentKind.TimerOnDelay or ComponentKind.TimerOffDelay or ComponentKind.TimerOnOffDelay or ComponentKind.Lamp or ComponentKind.BlinkingLamp or ComponentKind.Bell or ComponentKind.Siren or ComponentKind.Motor or ComponentKind.DcMotor or ComponentKind.AcMotor or ComponentKind.AcMotor1P) && IsEnergized(c.Id));
        Message = activeLoads > 0 ? LocalizationService.Get("Sim_Active_Loads", activeLoads) : LocalizationService.Get("Sim_Source_On_No_Loads");
    }

    private void MarkCurrentWires(ProjectDocument project)
    {
        _currentFlowSequences.Clear();
        _currentWires.Clear();
        _currentWireDirections.Clear();

        var activeLoads = project.Components.Where(c => IsLoad(c.Kind) && IsEnergized(c.Id)).ToList();
        if (activeLoads.Count == 0) return;

        var graph = new Dictionary<TerminalNode, List<(TerminalNode Next, Guid? WireId, bool Forward, Guid? ComponentId)>>();
        void AddEdge(TerminalNode a, TerminalNode b, Guid? wire, bool forward, Guid? component)
        {
            if (!graph.TryGetValue(a, out var list)) graph[a] = list = new();
            list.Add((b, wire, forward, component));
        }

        foreach (var wire in project.Wires)
        {
            var a = new TerminalNode(wire.StartComponentId, wire.StartTerminalId);
            var b = new TerminalNode(wire.EndComponentId, wire.EndTerminalId);
            AddEdge(a, b, wire.Id, true, null);
            AddEdge(b, a, wire.Id, false, null);
        }

        foreach (var pair in _conductingPairs)
        {
            AddEdge(pair.A, pair.B, null, true, pair.A.ComponentId);
            AddEdge(pair.B, pair.A, null, false, pair.A.ComponentId);
        }

        // Three-phase motor internal delta windings
        foreach (var motor in project.Components.Where(c => c.Kind == ComponentKind.AcMotor && IsEnergized(c.Id)))
        {
            foreach (var (a, b) in new[] { ("L1", "L2"), ("L2", "L3"), ("L3", "L1") })
            {
                AddEdge(new TerminalNode(motor.Id, a), new TerminalNode(motor.Id, b), null, true, motor.Id);
                AddEdge(new TerminalNode(motor.Id, b), new TerminalNode(motor.Id, a), null, false, motor.Id);
            }
        }

        foreach (var motor in project.Components.Where(c => c.Kind == ComponentKind.AcMotor1P && IsEnergized(c.Id)))
        {
            AddEdge(new TerminalNode(motor.Id, "L"), new TerminalNode(motor.Id, "N"), null, true, motor.Id);
            AddEdge(new TerminalNode(motor.Id, "N"), new TerminalNode(motor.Id, "L"), null, false, motor.Id);
        }

        var loadComponentIds = project.Components
            .Where(c => IsLoad(c.Kind))
            .Select(c => c.Id)
            .ToHashSet();

        List<(Guid WireId, bool Forward)>? FindRoute(TerminalNode start, TerminalNode finish, Guid requiredLoad)
        {
            var origin = (Node: start, SawLoad: false);
            var queue = new PriorityQueue<(TerminalNode Node, bool SawLoad, TerminalNode? PrevNode), int>();
            queue.Enqueue((start, false, null), 0);
            var bestCost = new Dictionary<(TerminalNode Node, bool SawLoad), int> { [origin] = 0 };
            var previous = new Dictionary<(TerminalNode Node, bool SawLoad), ((TerminalNode Node, bool SawLoad) Prev, Guid? WireId, bool Forward)>();
            (TerminalNode Node, bool SawLoad)? target = null;

            while (queue.TryDequeue(out var state, out var cost))
            {
                var key = (state.Node, state.SawLoad);
                if (bestCost.TryGetValue(key, out var recorded) && cost > recorded) continue;
                if (state.Node == finish && state.SawLoad) { target = key; break; }
                if (!graph.TryGetValue(state.Node, out var edges)) continue;

                foreach (var edge in edges)
                {
                    // Запрещаем разворот на 180° назад и повторное прохождение целевой нагрузки
                    if (state.PrevNode is { } prevNode && edge.Next == prevNode) continue;
                    if (edge.ComponentId == requiredLoad && state.SawLoad) continue;

                    var nextKey = (Node: edge.Next, SawLoad: state.SawLoad || edge.ComponentId == requiredLoad);
                    // Не срезаем путь через другие параллельные нагрузки (но последовательные всё равно пройдут)
                    var stepCost = edge.ComponentId is Guid cid && cid != requiredLoad && loadComponentIds.Contains(cid)
                        ? 1000
                        : 1;
                    var nextCost = cost + stepCost;
                    if (bestCost.TryGetValue(nextKey, out var existingCost) && nextCost >= existingCost) continue;

                    bestCost[nextKey] = nextCost;
                    previous[nextKey] = (key, edge.WireId, edge.Forward);
                    queue.Enqueue((edge.Next, nextKey.SawLoad, state.Node), nextCost);
                }
            }

            if (target is null) return null;
            var route = new List<(Guid WireId, bool Forward)>();
            var cursor = target.Value;
            while (cursor != origin)
            {
                var step = previous[cursor];
                if (step.WireId is Guid wireId) route.Add((wireId, step.Forward));
                cursor = step.Prev;
            }
            route.Reverse();
            return route;
        }

        // 1. Collect all single-phase / DC supply pairs: (Live, Return)
        var singlePhaseSupplies = new List<(TerminalNode Live, TerminalNode Return)>();

        foreach (var source in project.Components.Where(c => c.Kind == ComponentKind.AcSupply && _sourceEnabled.GetValueOrDefault(c.Id)))
        {
            var returnNode = new TerminalNode(source.Id, "R");
            if (source.Phases == 3)
            {
                singlePhaseSupplies.Add((new TerminalNode(source.Id, "L"), returnNode));
                singlePhaseSupplies.Add((new TerminalNode(source.Id, "L2"), returnNode));
                singlePhaseSupplies.Add((new TerminalNode(source.Id, "L3"), returnNode));
            }
            else
            {
                singlePhaseSupplies.Add((new TerminalNode(source.Id, "L"), returnNode));
            }
        }

        foreach (var source in project.Components.Where(c => c.Kind == ComponentKind.Supply24V && _sourceEnabled.GetValueOrDefault(c.Id)))
        {
            singlePhaseSupplies.Add((new TerminalNode(source.Id, "L"), new TerminalNode(source.Id, "R")));
        }

        foreach (var c in project.Components.Where(c => c.Kind is ComponentKind.PowerPhaseNeutral or ComponentKind.PowerPhaseNeutralEarth))
        {
            singlePhaseSupplies.Add((new TerminalNode(c.Id, "L"), new TerminalNode(c.Id, "N")));
        }

        var phases = project.Components.Where(c => c.Kind == ComponentKind.PowerPhase).ToList();
        var neutrals = project.Components.Where(c => c.Kind == ComponentKind.PowerNeutral).ToList();
        foreach (var phase in phases)
        {
            foreach (var neutral in neutrals)
            {
                singlePhaseSupplies.Add((new TerminalNode(phase.Id, "L"), new TerminalNode(neutral.Id, "N")));
            }
        }

        var pluses = project.Components.Where(c => c.Kind == ComponentKind.PowerPlus).ToList();
        var minuses = project.Components.Where(c => c.Kind == ComponentKind.PowerMinus).ToList();
        foreach (var plus in pluses)
        {
            foreach (var minus in minuses)
            {
                singlePhaseSupplies.Add((new TerminalNode(plus.Id, "+"), new TerminalNode(minus.Id, "−")));
            }
        }

        foreach (var c in project.Components.Where(c => c.Kind is ComponentKind.PowerThreePhaseNeutral or ComponentKind.PowerThreePhaseNeutralEarth))
        {
            singlePhaseSupplies.Add((new TerminalNode(c.Id, "L1"), new TerminalNode(c.Id, "N")));
            singlePhaseSupplies.Add((new TerminalNode(c.Id, "L2"), new TerminalNode(c.Id, "N")));
            singlePhaseSupplies.Add((new TerminalNode(c.Id, "L3"), new TerminalNode(c.Id, "N")));
        }

        foreach (var threePhase in project.Components.Where(c => c.Kind is ComponentKind.PowerThreePhase or ComponentKind.PowerThreePhaseEarth))
        {
            foreach (var neutral in neutrals)
            {
                singlePhaseSupplies.Add((new TerminalNode(threePhase.Id, "L1"), new TerminalNode(neutral.Id, "N")));
                singlePhaseSupplies.Add((new TerminalNode(threePhase.Id, "L2"), new TerminalNode(neutral.Id, "N")));
                singlePhaseSupplies.Add((new TerminalNode(threePhase.Id, "L3"), new TerminalNode(neutral.Id, "N")));
            }
        }

        // 2. Collect all three-phase sources: (P1, P2, P3)
        var threePhaseSources = new List<(TerminalNode P1, TerminalNode P2, TerminalNode P3)>();

        foreach (var source in project.Components.Where(c => c.Kind == ComponentKind.AcSupply && c.Phases == 3 && _sourceEnabled.GetValueOrDefault(c.Id)))
        {
            threePhaseSources.Add((new TerminalNode(source.Id, "L"), new TerminalNode(source.Id, "L2"), new TerminalNode(source.Id, "L3")));
        }

        foreach (var source in project.Components.Where(c => c.Kind is ComponentKind.PowerThreePhase or ComponentKind.PowerThreePhaseEarth
            or ComponentKind.PowerThreePhaseNeutral or ComponentKind.PowerThreePhaseNeutralEarth))
        {
            threePhaseSources.Add((new TerminalNode(source.Id, "L1"), new TerminalNode(source.Id, "L2"), new TerminalNode(source.Id, "L3")));
        }

        var activeThreePhaseLoads = activeLoads.Where(c => c.Kind == ComponentKind.AcMotor).ToList();
        var activeSinglePhaseLoads = activeLoads.Where(c => c.Kind != ComponentKind.AcMotor).ToList();

        // Find routes for single-phase / DC loads
        var singlePhaseRoutes = new List<List<(Guid WireId, bool Forward)>>();
        foreach (var load in activeSinglePhaseLoads)
        {
            List<(Guid WireId, bool Forward)>? bestRoute = null;
            foreach (var supply in singlePhaseSupplies)
            {
                var route = FindRoute(supply.Live, supply.Return, load.Id);
                if (route is { Count: > 0 })
                {
                    bestRoute = route;
                    break;
                }
            }

            // If not found with neutral, test line-to-line pairs
            if (bestRoute is null)
            {
                foreach (var src in threePhaseSources)
                {
                    var pairs = new[] { (src.P1, src.P2), (src.P2, src.P3), (src.P3, src.P1) };
                    foreach (var (from, to) in pairs)
                    {
                        var route = FindRoute(from, to, load.Id);
                        if (route is { Count: > 0 })
                        {
                            bestRoute = route;
                            break;
                        }
                    }
                    if (bestRoute is not null) break;
                }
            }

            if (bestRoute is not null) singlePhaseRoutes.Add(bestRoute);
        }

        // Find routes for three-phase loads: alternating departure phase P1 -> P2 -> P3
        // Формирование маршрутов для трёхфазной нагрузки: ток идёт по ВСЕМ трём фазам одновременно.
        // Шаг 0: втекает по P1 -> вытекает одновременно по P2 и P3
        // Шаг 1: втекает по P2 -> вытекает одновременно по P3 и P1
        // Шаг 2: втекает по P3 -> вытекает одновременно по P1 и P2
        var threePhaseStepRoutes = new List<List<(Guid WireId, bool Forward)>>[3]
        {
            new(),
            new(),
            new()
        };

        foreach (var motor in activeThreePhaseLoads)
        {
            foreach (var src in threePhaseSources)
            {
                // Шаг 0: из P1 в P2 и из P1 в P3
                var r0_12 = FindRoute(src.P1, src.P2, motor.Id);
                var r0_13 = FindRoute(src.P1, src.P3, motor.Id);

                // Шаг 1: из P2 в P3 и из P2 в P1
                var r1_23 = FindRoute(src.P2, src.P3, motor.Id);
                var r1_21 = FindRoute(src.P2, src.P1, motor.Id);

                // Шаг 2: из P3 в P1 и из P3 в P2
                var r2_31 = FindRoute(src.P3, src.P1, motor.Id);
                var r2_32 = FindRoute(src.P3, src.P2, motor.Id);

                if (r0_12 is not null || r0_13 is not null || r1_23 is not null || r2_31 is not null)
                {
                    if (r0_12 is not null) threePhaseStepRoutes[0].Add(r0_12);
                    if (r0_13 is not null) threePhaseStepRoutes[0].Add(r0_13);

                    if (r1_23 is not null) threePhaseStepRoutes[1].Add(r1_23);
                    if (r1_21 is not null) threePhaseStepRoutes[1].Add(r1_21);

                    if (r2_31 is not null) threePhaseStepRoutes[2].Add(r2_31);
                    if (r2_32 is not null) threePhaseStepRoutes[2].Add(r2_32);
                    break;
                }
            }
        }

        var hasThreePhaseRoutes = threePhaseStepRoutes.Any(step => step.Count > 0);

        if (hasThreePhaseRoutes)
        {
            for (var step = 0; step < 3; step++)
            {
                var frameRoutes = new List<List<(Guid WireId, bool Forward)>>();
                frameRoutes.AddRange(singlePhaseRoutes);
                frameRoutes.AddRange(threePhaseStepRoutes[step]);
                if (frameRoutes.Count > 0)
                    _currentFlowSequences.Add(frameRoutes);
            }
        }
        else if (singlePhaseRoutes.Count > 0)
        {
            _currentFlowSequences.Add(singlePhaseRoutes);
        }

        _currentFlowSequenceIndex = 0;
        RefreshCurrentFlowWires();
    }

    private void RefreshCurrentFlowWires()
    {
        _currentWires.Clear();
        _currentWireDirections.Clear();
        if (_currentFlowSequences.Count == 0) return;
        var index = Math.Clamp(_currentFlowSequenceIndex, 0, _currentFlowSequences.Count - 1);
        foreach (var route in _currentFlowSequences[index])
        {
            foreach (var (wireId, forward) in route)
            {
                _currentWires.Add(wireId);
                _currentWireDirections[wireId] = forward;
            }
        }
    }

    private static bool IsLoad(ComponentKind kind) => kind is ComponentKind.Coil or ComponentKind.Lamp or
        ComponentKind.BlinkingLamp or ComponentKind.Bell or ComponentKind.Siren or ComponentKind.Motor or ComponentKind.DcMotor or
        ComponentKind.AcMotor or ComponentKind.AcMotor1P or ComponentKind.TimerOnDelay or ComponentKind.TimerOffDelay or ComponentKind.TimerOnOffDelay;

    private SolveResult? SolveNodal(ProjectDocument project)
    {
        var union = BuildWireUnion(project);
        // Wires are ideal nodes. Switches and contacts remain finite low-resistance elements,
        // so voltage drop and short-circuit current can be solved instead of shorting nodes together.
        foreach (var source in project.Components.Where(c => IsSource(c.Kind) && _sourceEnabled.GetValueOrDefault(c.Id)))
        {
            var neutralRoot = union.Find(new TerminalNode(source.Id, "R"));
            var shorted = GetLiveTerminalIds(source).Any(t => union.Find(new TerminalNode(source.Id, t)) == neutralRoot);
            if (shorted)
            {
                var liveTerminal = GetLiveTerminalIds(source)
                    .FirstOrDefault(t => union.Find(new TerminalNode(source.Id, t)) == neutralRoot);
                if (liveTerminal is not null)
                    AddShortCircuitPoint(FindWirePathMidpoint(project, new TerminalNode(source.Id, liveTerminal), new TerminalNode(source.Id, "R")));
                var protection = project.Components.FirstOrDefault(c => c.Kind == ComponentKind.CircuitBreaker && _protectiveClosed.GetValueOrDefault(c.Id));
                if (protection is not null)
                {
                    _protectiveClosed[protection.Id] = false;
                    _states[protection.Id] = LocalizationService.Get("Sim_State_Short_Tripped");
                    return SolveNodal(project);
                }
                HasShortCircuit = true;
                return null;
            }
        }

        var allNodes = new HashSet<TerminalNode>();
        foreach (var c in project.Components)
            foreach (var term in ComponentTerminals.Get(c)) allNodes.Add(new TerminalNode(c.Id, term.Id));

        var rootSet = allNodes.Select(union.Find).Distinct().ToList();
        if (rootSet.Count == 0) return null;
        var activeSources = project.Components.Where(c => IsSource(c.Kind) && _sourceEnabled.GetValueOrDefault(c.Id)).ToList();
        var hasExplicitThreePhaseSource = activeSources.Any(c => c.Kind == ComponentKind.AcSupply && c.Phases == 3);
        var markerSupplies = hasExplicitThreePhaseSource
            ? new List<PassiveMarkerSource>()
            : GetHealthyPassiveMarkerSupplies(project, union);
        if (activeSources.Count == 0 && markerSupplies.Count == 0) return null;

        var referenceRoot = activeSources.Count > 0
            ? union.Find(new TerminalNode(activeSources[0].Id, "R"))
            : union.Find(new TerminalNode(markerSupplies[0].ReturnComponent.Id, markerSupplies[0].ReturnTerminalId));
        var nodeIndex = new Dictionary<TerminalNode, int>();
        var next = 0;
        foreach (var root in rootSet)
            if (root != referenceRoot) nodeIndex[root] = next++;
        var nodeCount = next;

        var resistors = new List<(TerminalNode A, TerminalNode B, double R)>();
        foreach (var c in project.Components)
        {
            if (c.Kind is ComponentKind.AcSupply or ComponentKind.Supply24V ||
                ComponentTerminals.IsPowerMarker(c.Kind)) continue;
            var r = ResistanceFor(project, c);
            var terminals = ComponentTerminals.Get(c);
            if (c.Kind == ComponentKind.CrossoverSwitch)
            {
                var crossed = _switchClosed.GetValueOrDefault(c.Id);
                var closedPairs = crossed
                    ? new[] { ("L1", "R2"), ("L2", "R1") }
                    : new[] { ("L1", "R1"), ("L2", "R2") };
                var allPairs = new[] { ("L1", "R1"), ("L1", "R2"), ("L2", "R1"), ("L2", "R2") };
                foreach (var pair in allPairs)
                {
                    var resistance = closedPairs.Contains(pair) ? ClosedContactResistance : OpenResistance;
                    resistors.Add((new TerminalNode(c.Id, pair.Item1), new TerminalNode(c.Id, pair.Item2), resistance));
                }
                continue;
            }
            if (c.Kind is ComponentKind.ConContact or ComponentKind.PushButtonCon or ComponentKind.ToggleSwitchCon)
            {
                var useNoTerminal = c.Kind == ComponentKind.PushButtonCon
                    ? _pressedButtons.GetValueOrDefault(c.Id)
                    : c.Kind == ComponentKind.ToggleSwitchCon
                        ? _switchClosed.GetValueOrDefault(c.Id)
                        : IsAuxContactClosed(project, c);
                var selectedTerminal = useNoTerminal ? "NO" : "NC";
                var otherTerminal = selectedTerminal == "NO" ? "NC" : "NO";
                resistors.Add((new TerminalNode(c.Id, "C"), new TerminalNode(c.Id, selectedTerminal), ClosedContactResistance));
                resistors.Add((new TerminalNode(c.Id, "C"), new TerminalNode(c.Id, otherTerminal), OpenResistance));
                continue;
            }
            if (c.Kind == ComponentKind.AcMotor)
            {
                foreach (var pair in new[] { ("L1", "L2"), ("L2", "L3"), ("L3", "L1") })
                    resistors.Add((new TerminalNode(c.Id, pair.Item1), new TerminalNode(c.Id, pair.Item2), r));
                continue;
            }
            if (c.Kind == ComponentKind.AcMotor1P)
            {
                resistors.Add((new TerminalNode(c.Id, "L"), new TerminalNode(c.Id, "N"), r));
                continue;
            }
            if (terminals.Any(t => t.Id == "L") && terminals.Any(t => t.Id == "R"))
                resistors.Add((new TerminalNode(c.Id, "L"), new TerminalNode(c.Id, "R"), r));
            else
                for (var i = 0; i + 1 < terminals.Count; i += 2)
                {
                    var pairResistance = r;
                    if (c.Kind is ComponentKind.AuxContactNO_NC or ComponentKind.TimerOnOffContactNO_NC)
                    {
                        var noClosed = IsAuxContactClosed(project, new ComponentModel { Id = c.Id, Kind = ComponentKind.AuxContactNO, Tag = c.Tag });
                        pairResistance = (i == 0 ? !noClosed : noClosed) ? ClosedContactResistance : OpenResistance;
                    }
                    else if (c.Kind is ComponentKind.PushButtonNO_NC or ComponentKind.ToggleSwitchNO_NC or ComponentKind.ThermalRelayAuxNO_NC)
                    {
                        var active = c.Kind switch
                        {
                            ComponentKind.PushButtonNO_NC => _pressedButtons.GetValueOrDefault(c.Id),
                            ComponentKind.ToggleSwitchNO_NC => _switchClosed.GetValueOrDefault(c.Id),
                            _ => IsThermalRelayTripped(project, c.Tag)
                        };
                        pairResistance = (i == 0 ? !active : active) ? ClosedContactResistance : OpenResistance;
                    }
                    resistors.Add((new TerminalNode(c.Id, terminals[i].Id), new TerminalNode(c.Id, terminals[i + 1].Id), pairResistance));
                }
        }

        _conductingPairs = resistors.Where(r => r.R < OpenResistance * 0.5)
            .Select(r => (r.A, r.B)).ToList();

        var shortUnion = new UnionFind();
        foreach (var component in project.Components)
            foreach (var terminal in ComponentTerminals.Get(component))
                shortUnion.Add(new TerminalNode(component.Id, terminal.Id));
        foreach (var wire in project.Wires)
            shortUnion.Union(new TerminalNode(wire.StartComponentId, wire.StartTerminalId), new TerminalNode(wire.EndComponentId, wire.EndTerminalId));
        foreach (var resistor in resistors.Where(r => r.R <= 0.5))
            shortUnion.Union(resistor.A, resistor.B);

        bool HasLowResistanceShort(TerminalNode live, TerminalNode neutral) => shortUnion.Find(live) == shortUnion.Find(neutral);
        void MarkLowResistanceShort(TerminalNode live, TerminalNode neutral)
        {
            if (!HasLowResistanceShort(live, neutral)) return;
            HasShortCircuit = true;
            if (_shortCircuitPoints.Count == 0)
            {
                var faultComponent = resistors.Where(r => r.R <= 0.5)
                    .Where(r => shortUnion.Find(r.A) == shortUnion.Find(live))
                    .Select(r => project.Components.FirstOrDefault(c => c.Id == r.A.ComponentId))
                    .FirstOrDefault(c => c is not null);
                if (faultComponent is not null) AddShortCircuitPoint((faultComponent.X, faultComponent.Y));
            }
        }
        foreach (var source in activeSources)
        {
            var neutral = new TerminalNode(source.Id, "R");
            var liveIds = GetLiveTerminalIds(source).ToList();
            foreach (var terminal in liveIds)
                MarkLowResistanceShort(new TerminalNode(source.Id, terminal), neutral);
            for (var i = 0; i < liveIds.Count; i++)
            for (var j = i + 1; j < liveIds.Count; j++)
                MarkLowResistanceShort(new TerminalNode(source.Id, liveIds[i]), new TerminalNode(source.Id, liveIds[j]));
        }
        foreach (var marker in markerSupplies)
            MarkLowResistanceShort(new TerminalNode(marker.LiveComponent.Id, marker.LiveTerminalId),
                new TerminalNode(marker.ReturnComponent.Id, marker.ReturnTerminalId));
        for (var i = 0; i < markerSupplies.Count; i++)
        for (var j = i + 1; j < markerSupplies.Count; j++)
            if (Complex.Abs(markerSupplies[i].Voltage - markerSupplies[j].Voltage) > 5)
                MarkLowResistanceShort(new TerminalNode(markerSupplies[i].LiveComponent.Id, markerSupplies[i].LiveTerminalId),
                    new TerminalNode(markerSupplies[j].LiveComponent.Id, markerSupplies[j].LiveTerminalId));

        var voltageSources = new List<(TerminalNode A, TerminalNode B, Complex V)>();
        foreach (var source in activeSources)
        {
            foreach (var phase in GetSourcePhasors(source))
                voltageSources.Add((new TerminalNode(source.Id, phase.TerminalId), new TerminalNode(source.Id, "R"), phase.Voltage));
        }
        foreach (var markers in markerSupplies)
            voltageSources.Add((new TerminalNode(markers.LiveComponent.Id, markers.LiveTerminalId),
                new TerminalNode(markers.ReturnComponent.Id, markers.ReturnTerminalId), markers.Voltage));

        var dimension = nodeCount + voltageSources.Count;
        if (dimension == 0) return null;
        var matrix = new Complex[dimension, dimension];
        var rhs = new Complex[dimension];
        const double leakageG = 1e-10;
        for (var i = 0; i < nodeCount; i++) matrix[i, i] += new Complex(leakageG, 0);

        void AddConductance(TerminalNode ta, TerminalNode tb, double resistance)
        {
            var aRoot = union.Find(ta); var bRoot = union.Find(tb);
            if (aRoot == bRoot) return;
            var g = 1.0 / Math.Max(1e-9, resistance);
            var ai = nodeIndex.TryGetValue(aRoot, out var ax) ? ax : -1;
            var bi = nodeIndex.TryGetValue(bRoot, out var bx) ? bx : -1;
            if (ai >= 0) matrix[ai, ai] += new Complex(g, 0);
            if (bi >= 0) matrix[bi, bi] += new Complex(g, 0);
            if (ai >= 0 && bi >= 0) { matrix[ai, bi] -= g; matrix[bi, ai] -= g; }
        }

        foreach (var resistor in resistors) AddConductance(resistor.A, resistor.B, resistor.R);

        for (var k = 0; k < voltageSources.Count; k++)
        {
            var src = voltageSources[k];
            var ai = nodeIndex.TryGetValue(union.Find(src.A), out var ax) ? ax : -1;
            var bi = nodeIndex.TryGetValue(union.Find(src.B), out var bx) ? bx : -1;
            var ci = nodeCount + k;
            if (ai >= 0) { matrix[ai, ci] += Complex.One; matrix[ci, ai] += Complex.One; }
            if (bi >= 0) { matrix[bi, ci] -= Complex.One; matrix[ci, bi] -= Complex.One; }
            rhs[ci] = src.V;
        }

        var solution = GaussianSolve(matrix, rhs);
        if (solution is null || solution.Any(v => double.IsNaN(v.Real) || double.IsNaN(v.Imaginary) || double.IsInfinity(v.Real) || double.IsInfinity(v.Imaginary) || Complex.Abs(v) > 1e9)) return null;

        var terminalVoltages = new Dictionary<TerminalNode, Complex>();
        foreach (var term in allNodes)
        {
            var root = union.Find(term);
            terminalVoltages[term] = nodeIndex.TryGetValue(root, out var idx) ? solution[idx] : Complex.Zero;
        }

        var breakerCurrents = project.Components
            .Where(c => c.Kind == ComponentKind.CircuitBreaker && _protectiveClosed.GetValueOrDefault(c.Id))
            .Select(c =>
            {
                var va = terminalVoltages.GetValueOrDefault(new TerminalNode(c.Id, "L"));
                var vb = terminalVoltages.GetValueOrDefault(new TerminalNode(c.Id, "R"));
                var resistance = Math.Max(ClosedContactResistance, ResistanceFor(project, c));
                return (Component: c, Current: Complex.Abs(va - vb) / resistance);
            })
            .OrderByDescending(x => x.Current)
            .ToList();
        var trippedBreaker = breakerCurrents.FirstOrDefault(x => x.Current > 40);
        if (trippedBreaker.Component is not null)
        {
            _protectiveClosed[trippedBreaker.Component.Id] = false;
            _states[trippedBreaker.Component.Id] = LocalizationService.Get("Sim_State_Overcurrent_Tripped");
            return SolveNodal(project);
        }

        var maxSourceCurrent = 0.0;
        for (var k = 0; k < voltageSources.Count; k++) maxSourceCurrent = Math.Max(maxSourceCurrent, Complex.Abs(solution[nodeCount + k]));
        if (maxSourceCurrent > 80)
        {
            HasShortCircuit = true;
            return null;
        }

        return new SolveResult(terminalVoltages, union);
    }

    private UnionFind BuildWireUnion(ProjectDocument project)
    {
        var union = new UnionFind();
        foreach (var c in project.Components)
            foreach (var t in ComponentTerminals.Get(c)) union.Add(new TerminalNode(c.Id, t.Id));
        foreach (var wire in project.Wires)
        {
            if (project.Components.Any(c => c.Id == wire.StartComponentId) && project.Components.Any(c => c.Id == wire.EndComponentId))
                union.Union(new TerminalNode(wire.StartComponentId, wire.StartTerminalId), new TerminalNode(wire.EndComponentId, wire.EndTerminalId));
        }
        return union;
    }

    private static (double X, double Y)? FindWirePathMidpoint(ProjectDocument project, TerminalNode start, TerminalNode end)
    {
        var previous = new Dictionary<TerminalNode, TerminalNode?> { [start] = null };
        var queue = new Queue<TerminalNode>();
        queue.Enqueue(start);
        while (queue.Count > 0 && !previous.ContainsKey(end))
        {
            var current = queue.Dequeue();
            foreach (var wire in project.Wires)
            {
                TerminalNode next;
                if (wire.StartComponentId == current.ComponentId && wire.StartTerminalId == current.TerminalId)
                    next = new TerminalNode(wire.EndComponentId, wire.EndTerminalId);
                else if (wire.EndComponentId == current.ComponentId && wire.EndTerminalId == current.TerminalId)
                    next = new TerminalNode(wire.StartComponentId, wire.StartTerminalId);
                else continue;
                if (previous.ContainsKey(next)) continue;
                previous[next] = current;
                queue.Enqueue(next);
            }
        }

        if (!previous.ContainsKey(end)) return null;
        var path = new List<TerminalNode>();
        for (TerminalNode? node = end; node is not null; node = previous[node.Value]) path.Add(node.Value);
        path.Reverse();
        var middle = path[path.Count / 2];
        var component = project.Components.FirstOrDefault(c => c.Id == middle.ComponentId);
        if (component is null) return null;
        var terminal = ComponentTerminals.Get(component).FirstOrDefault(t => t.Id == middle.TerminalId);
        if (terminal is null) return (component.X, component.Y);
        var angle = component.Rotation * Math.PI / 180.0;
        return (component.X + terminal.OffsetX * Math.Cos(angle) - terminal.OffsetY * Math.Sin(angle),
            component.Y + terminal.OffsetX * Math.Sin(angle) + terminal.OffsetY * Math.Cos(angle));
    }

    private bool IsLoadPowered(ComponentModel c, SolveResult result, ProjectDocument project)
    {
        if (c.Kind is not (ComponentKind.Coil or ComponentKind.Lamp or ComponentKind.BlinkingLamp or ComponentKind.Bell or ComponentKind.Siren or ComponentKind.Motor or 
        ComponentKind.DcMotor or ComponentKind.AcMotor or ComponentKind.AcMotor1P or ComponentKind.TimerOnDelay or ComponentKind.TimerOffDelay or ComponentKind.TimerOnOffDelay)) return false;
        if (c.Kind == ComponentKind.DcMotor &&
            !project.Components.Any(x => x.Kind == ComponentKind.Supply24V && _sourceEnabled.GetValueOrDefault(x.Id)) &&
            !GetPassiveMarkerSupplies(project).Any(x => Math.Abs(Complex.Abs(x.Voltage) - 24) < 0.1)) return false;
        if (c.Kind is ComponentKind.AcMotor or ComponentKind.AcMotor1P)
        {
            var explicitAcSource = project.Components.Any(x => x.Kind == ComponentKind.AcSupply && _sourceEnabled.GetValueOrDefault(x.Id));
            var passiveAcSources = GetPassiveMarkerSupplies(project).Where(x => Complex.Abs(x.Voltage) > 100).ToList();
            if (!explicitAcSource && passiveAcSources.Count == 0) return false;
            var phases = ComponentTerminals.Get(c).Select(t => result.TerminalVoltages.GetValueOrDefault(new TerminalNode(c.Id, t.Id))).ToList();
            if (c.Kind == ComponentKind.AcMotor &&
                !project.Components.Any(x => x.Kind == ComponentKind.AcSupply && x.Phases == 3 && _sourceEnabled.GetValueOrDefault(x.Id)) &&
                !project.Components.Any(x => x.Kind is ComponentKind.PowerThreePhase or ComponentKind.PowerThreePhaseEarth or ComponentKind.PowerThreePhaseNeutral or ComponentKind.PowerThreePhaseNeutralEarth)) return false;
            return phases.Any(a => phases.Any(b => Complex.Abs(a - b) >= 5));
        }
        var (left, right) = (c.Kind == ComponentKind.Coil || IsTimer(c.Kind)) ? ("A1", "A2") : ("L", "R");
        var va = result.TerminalVoltages.GetValueOrDefault(new TerminalNode(c.Id, left));
        var vb = result.TerminalVoltages.GetValueOrDefault(new TerminalNode(c.Id, right));
        return Complex.Abs(va - vb) >= 5.0;
    }

    private bool HasLiveTerminalVoltage(ComponentModel component) => ComponentTerminals.Get(component)
        .Any(t => _terminalVoltages.GetValueOrDefault(VoltageKey(component.Id, t.Id)) >= 8);

    private double ResistanceFor(ProjectDocument project, ComponentModel c) => c.Kind switch
    {
        ComponentKind.Lamp or ComponentKind.BlinkingLamp or ComponentKind.Bell or ComponentKind.Siren => 900,
        ComponentKind.Fuse => _blownFuses.Contains(c.Id) ? OpenResistance : ClosedContactResistance,
        ComponentKind.Coil or ComponentKind.TimerOnDelay or ComponentKind.TimerOffDelay or ComponentKind.TimerOnOffDelay => 600,
        ComponentKind.LimitSwitchNO or ComponentKind.LimitSwitchNC => _switchClosed.GetValueOrDefault(c.Id) ? ClosedContactResistance : OpenResistance,
        ComponentKind.PushButtonNO => _pressedButtons.GetValueOrDefault(c.Id) ? ClosedContactResistance : OpenResistance,
        ComponentKind.PushButtonNC => _pressedButtons.GetValueOrDefault(c.Id) ? OpenResistance : ClosedContactResistance,
        ComponentKind.ToggleSwitchNO or ComponentKind.ToggleSwitchNC => _switchClosed.GetValueOrDefault(c.Id) ? ClosedContactResistance : OpenResistance,
        ComponentKind.AuxContactNO or ComponentKind.AuxContactNC or ComponentKind.AuxContactNO_NC or ComponentKind.ConContact or ComponentKind.OverloadNO => IsAuxContactClosed(project, c) ? ClosedContactResistance : OpenResistance,
        ComponentKind.AuxContactNO or ComponentKind.AuxContactNC or ComponentKind.AuxContactNO_NC
            or ComponentKind.TimerOnContactNO or ComponentKind.TimerOnContactNC
            or ComponentKind.TimerOffContactNO or ComponentKind.TimerOffContactNC
            or ComponentKind.TimerOnOffContactNO_NC
            or ComponentKind.ConContact or ComponentKind.OverloadNO => IsAuxContactClosed(project, c) ? ClosedContactResistance : OpenResistance,
        ComponentKind.ThermalRelayAuxNC => IsAuxContactClosed(project, new ComponentModel { Id = c.Id, Kind = ComponentKind.AuxContactNC, Tag = c.Tag }) ? ClosedContactResistance : OpenResistance,
        ComponentKind.Contactor2P or ComponentKind.Contactor3P or ComponentKind.Contactor4P => IsContactorClosed(c.Id, project) ? ClosedContactResistance : OpenResistance,
        ComponentKind.OverloadNC => _overloadTripped.GetValueOrDefault(c.Id) ? OpenResistance : ClosedContactResistance,
        ComponentKind.CircuitBreaker or ComponentKind.DifferentialBreaker or ComponentKind.Breaker1P or ComponentKind.Breaker1PN or ComponentKind.Breaker2P or ComponentKind.Breaker3P or ComponentKind.Breaker3PN or ComponentKind.Rcd1PN or ComponentKind.Rcd3PN or ComponentKind.ThermalRelay => _protectiveClosed.GetValueOrDefault(c.Id) ? ClosedContactResistance : OpenResistance,
        ComponentKind.Motor => 35,
        ComponentKind.DcMotor or ComponentKind.AcMotor or ComponentKind.AcMotor1P => 900,
        _ => OpenResistance
    };

    private bool IsThermalRelayTripped(ProjectDocument project, string tag)
    {
        var relays = project.Components.Where(c => c.Kind == ComponentKind.ThermalRelay && TagMatches(c, tag)).ToList();
        if (relays.Count > 0) return relays.Any(c => _thermalRelayTripped.GetValueOrDefault(c.Id));
        var overloads = project.Components.Where(c => (c.Kind is ComponentKind.OverloadNC or ComponentKind.OverloadNO) && TagMatches(c, tag)).ToList();
        return overloads.Any(c => _overloadTripped.GetValueOrDefault(c.Id));
    }

    private bool IsAuxContactClosed(ProjectDocument project, ComponentModel contact)
    {
        var isNormallyOpen = contact.Kind is ComponentKind.AuxContactNO or ComponentKind.AuxContactNO_NC
            or ComponentKind.TimerOnContactNO or ComponentKind.TimerOffContactNO or ComponentKind.TimerOnOffContactNO_NC
            or ComponentKind.ConContact;

        var matchingThermalRelays = project.Components
            .Where(c => c.Kind == ComponentKind.ThermalRelay && TagMatches(c, contact.Tag))
            .ToList();
        if (matchingThermalRelays.Count > 0)
        {
            var tripped = matchingThermalRelays.Any(c => _thermalRelayTripped.GetValueOrDefault(c.Id));
            return isNormallyOpen || contact.Kind == ComponentKind.OverloadNO ? tripped : !tripped;
        }

        var matchingButtons = project.Components
            .Where(c => IsButton(c.Kind) && TagMatches(c, contact.Tag))
            .ToList();
        if (matchingButtons.Count > 0)
        {
            var pressed = matchingButtons.Any(c => _pressedButtons.GetValueOrDefault(c.Id));
            return isNormallyOpen ? pressed : !pressed;
        }

        var matchingCoils = project.Components
            .Where(c => c.Kind == ComponentKind.Coil && TagMatches(c, contact.Tag))
            .ToList();
        if (matchingCoils.Count > 0)
        {
            var energized = matchingCoils.Any(c => _coilEnergized.GetValueOrDefault(c.Id));
            return isNormallyOpen ? energized : !energized;
        }

        var matchingTimers = project.Components
            .Where(c => IsTimer(c.Kind) && TagMatches(c, contact.Tag))
            .ToList();
        if (matchingTimers.Count > 0)
        {
            var active = matchingTimers.Any(c => IsTimerOutputActive(c.Id));
            return isNormallyOpen ? active : !active;
        }

        var matchingOverloads = project.Components
            .Where(c => (c.Kind is ComponentKind.OverloadNC or ComponentKind.OverloadNO) && TagMatches(c, contact.Tag))
            .ToList();
        if (matchingOverloads.Count > 0)
        {
            var tripped = matchingOverloads.Any(c => _overloadTripped.GetValueOrDefault(c.Id));
            return isNormallyOpen || contact.Kind == ComponentKind.OverloadNO ? tripped : !tripped;
        }

        var matchingProtective = project.Components
            .Where(c => c.Kind is ComponentKind.CircuitBreaker or ComponentKind.DifferentialBreaker or ComponentKind.Breaker1P or ComponentKind.Breaker1PN or ComponentKind.Breaker2P or ComponentKind.Breaker3P or ComponentKind.Breaker3PN or ComponentKind.Rcd1PN or ComponentKind.Rcd3PN && TagMatches(c, contact.Tag))
            .ToList();
        if (matchingProtective.Count > 0)
        {
            var enabled = matchingProtective.Any(c => _protectiveClosed.GetValueOrDefault(c.Id));
            return isNormallyOpen ? enabled : !enabled;
        }

        var matchingSources = project.Components.Where(c => IsSource(c.Kind) && TagMatches(c, contact.Tag)).ToList();
        if (matchingSources.Count > 0)
        {
            var enabled = matchingSources.Any(c => _sourceEnabled.GetValueOrDefault(c.Id));
            return isNormallyOpen ? enabled : !enabled;
        }

        return contact.Kind is ComponentKind.AuxContactNC or ComponentKind.TimerOnContactNC or ComponentKind.TimerOffContactNC;
    }

    private static bool IsButton(ComponentKind k) => k is ComponentKind.PushButtonNO or ComponentKind.PushButtonNC or ComponentKind.PushButtonNO_NC or ComponentKind.PushButtonCon;
    private static bool IsContactSwitch(ComponentKind k) => k is ComponentKind.ToggleSwitchNO or ComponentKind.ToggleSwitchNC or ComponentKind.ToggleSwitchNO_NC or ComponentKind.ToggleSwitchCon or ComponentKind.CrossoverSwitch;
    private static bool IsProtective(ComponentKind k) => k is ComponentKind.CircuitBreaker or ComponentKind.DifferentialBreaker or ComponentKind.Breaker1P or ComponentKind.Breaker1PN or ComponentKind.Breaker2P or ComponentKind.Breaker3P or ComponentKind.Breaker3PN or ComponentKind.Rcd1PN or ComponentKind.Rcd3PN or ComponentKind.ThermalRelay;
    private static bool IsSource(ComponentKind k) => k is ComponentKind.AcSupply or ComponentKind.Supply24V;

    private sealed record PassiveMarkerSource(ComponentModel LiveComponent, string LiveTerminalId,
        ComponentModel ReturnComponent, string ReturnTerminalId, Complex Voltage);

    private List<PassiveMarkerSource> GetHealthyPassiveMarkerSupplies(ProjectDocument project, UnionFind union)
    {
        var sources = GetPassiveMarkerSupplies(project);
        var faulted = new HashSet<int>();
        for (var i = 0; i < sources.Count; i++)
        {
            var live = new TerminalNode(sources[i].LiveComponent.Id, sources[i].LiveTerminalId);
            var ground = new TerminalNode(sources[i].ReturnComponent.Id, sources[i].ReturnTerminalId);
            if (union.Find(live) != union.Find(ground)) continue;
            faulted.Add(i);
            HasShortCircuit = true;
            AddShortCircuitPoint(FindWirePathMidpoint(project, live, ground));
        }

        foreach (var group in sources.Select((source, index) => (source, index))
                     .GroupBy(item => union.Find(new TerminalNode(item.source.ReturnComponent.Id, item.source.ReturnTerminalId))))
        {
            foreach (var phaseGroup in group.GroupBy(item => union.Find(new TerminalNode(item.source.LiveComponent.Id, item.source.LiveTerminalId)))
                         .Where(g => g.Count() > 1))
            {
                var collided = phaseGroup.ToList();
                for (var i = 0; i < collided.Count; i++)
                for (var j = i + 1; j < collided.Count; j++)
                {
                    if (Complex.Abs(collided[i].source.Voltage - collided[j].source.Voltage) <= 5) continue;
                    faulted.Add(collided[i].index); faulted.Add(collided[j].index);
                    HasShortCircuit = true;
                    var a = new TerminalNode(collided[i].source.LiveComponent.Id, collided[i].source.LiveTerminalId);
                    var b = new TerminalNode(collided[j].source.LiveComponent.Id, collided[j].source.LiveTerminalId);
                    AddShortCircuitPoint(FindWirePathMidpoint(project, a, b));
                }
            }
        }
        var healthy = sources.Where((_, index) => !faulted.Contains(index)).ToList();
        var unique = new List<PassiveMarkerSource>();
        foreach (var source in healthy)
        {
            var live = union.Find(new TerminalNode(source.LiveComponent.Id, source.LiveTerminalId));
            var ground = union.Find(new TerminalNode(source.ReturnComponent.Id, source.ReturnTerminalId));
            if (unique.Any(existing =>
                union.Find(new TerminalNode(existing.LiveComponent.Id, existing.LiveTerminalId)) == live &&
                union.Find(new TerminalNode(existing.ReturnComponent.Id, existing.ReturnTerminalId)) == ground &&
                Complex.Abs(existing.Voltage - source.Voltage) <= 5)) continue;
            unique.Add(source);
        }
        return unique;
    }

    private void AddShortCircuitPoint((double X, double Y)? point)
    {
        if (point is not { } location) return;
        if (_shortCircuitPoints.Any(existing => Math.Abs(existing.X - location.X) < 5 && Math.Abs(existing.Y - location.Y) < 5)) return;
        _shortCircuitPoints.Add(location);
    }

    private static List<PassiveMarkerSource> GetPassiveMarkerSupplies(ProjectDocument project)
    {
        var supplies = new List<PassiveMarkerSource>();
        foreach (var singlePhase in project.Components.Where(c => c.Kind is ComponentKind.PowerPhaseNeutral or ComponentKind.PowerPhaseNeutralEarth))
            supplies.Add(new PassiveMarkerSource(singlePhase, "L", singlePhase, "N", new Complex(230, 0)));
        var phases = project.Components.Where(c => c.Kind == ComponentKind.PowerPhase).ToList();
        var neutrals = project.Components.Where(c => c.Kind == ComponentKind.PowerNeutral).ToList();
        foreach (var phase in phases)
        {
            var neutral = neutrals.OrderBy(n => DistanceSquared(phase, n)).FirstOrDefault();
            if (neutral is not null) supplies.Add(new PassiveMarkerSource(phase, "L", neutral, "N", new Complex(230, 0)));
        }
        var pluses = project.Components.Where(c => c.Kind == ComponentKind.PowerPlus).ToList();
        var minuses = project.Components.Where(c => c.Kind == ComponentKind.PowerMinus).ToList();
        foreach (var plus in pluses)
        {
            var minus = minuses.OrderBy(m => DistanceSquared(plus, m)).FirstOrDefault();
            if (minus is not null) supplies.Add(new PassiveMarkerSource(plus, "+", minus, "−", new Complex(24, 0)));
        }

        foreach (var composite in project.Components.Where(c => c.Kind is ComponentKind.PowerThreePhaseNeutral or ComponentKind.PowerThreePhaseNeutralEarth))
        {
            supplies.Add(new PassiveMarkerSource(composite, "L1", composite, "N", Complex.FromPolarCoordinates(230, 0)));
            supplies.Add(new PassiveMarkerSource(composite, "L2", composite, "N", Complex.FromPolarCoordinates(230, -2 * Math.PI / 3)));
            supplies.Add(new PassiveMarkerSource(composite, "L3", composite, "N", Complex.FromPolarCoordinates(230, 2 * Math.PI / 3)));
        }
        foreach (var threePhase in project.Components.Where(c => c.Kind is ComponentKind.PowerThreePhase or ComponentKind.PowerThreePhaseEarth))
        {
            var neutral = neutrals.OrderBy(n => DistanceSquared(threePhase, n)).FirstOrDefault();
            if (neutral is not null)
            {
                supplies.Add(new PassiveMarkerSource(threePhase, "L1", neutral, "N", Complex.FromPolarCoordinates(230, 0)));
                supplies.Add(new PassiveMarkerSource(threePhase, "L2", neutral, "N", Complex.FromPolarCoordinates(230, -2 * Math.PI / 3)));
                supplies.Add(new PassiveMarkerSource(threePhase, "L3", neutral, "N", Complex.FromPolarCoordinates(230, 2 * Math.PI / 3)));
            }
            else
            {
                supplies.Add(new PassiveMarkerSource(threePhase, "L1", threePhase, "L3", Complex.FromPolarCoordinates(400, -Math.PI / 6)));
                supplies.Add(new PassiveMarkerSource(threePhase, "L2", threePhase, "L3", Complex.FromPolarCoordinates(400, -Math.PI / 2)));
            }
        }
        return supplies;
    }

    private static double DistanceSquared(ComponentModel a, ComponentModel b) =>
        Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2);

    private static IEnumerable<(string TerminalId, Complex Voltage)> GetSourcePhasors(ComponentModel source)
    {
        if (source.Kind == ComponentKind.Supply24V)
        {
            yield return ("L", new Complex(24, 0));
            yield break;
        }
        if (source.Phases == 3)
        {
            var phaseVoltage = source.Voltage == 400 ? 400 / Math.Sqrt(3) : source.Voltage;
            yield return ("L", Complex.FromPolarCoordinates(phaseVoltage, 0));
            yield return ("L2", Complex.FromPolarCoordinates(phaseVoltage, -2 * Math.PI / 3));
            yield return ("L3", Complex.FromPolarCoordinates(phaseVoltage, 2 * Math.PI / 3));
            yield break;
        }
        yield return ("L", new Complex(source.Voltage, 0));
    }

    private static IEnumerable<string> GetLiveTerminalIds(ComponentModel source)
    {
        if (source.Kind == ComponentKind.AcSupply && source.Phases == 3) { yield return "L"; yield return "L2"; yield return "L3"; }
        else yield return "L";
    }

private void UpdateStatuses(ProjectDocument project)
    {
        foreach (var c in project.Components)
        {
            if (c.Kind is ComponentKind.AuxContactNO or ComponentKind.AuxContactNC or ComponentKind.AuxContactNO_NC
                or ComponentKind.TimerOnContactNO or ComponentKind.TimerOnContactNC
                or ComponentKind.TimerOffContactNO or ComponentKind.TimerOffContactNC
                or ComponentKind.TimerOnOffContactNO_NC or ComponentKind.OverloadNO)
                _auxClosed[c.Id] = IsAuxContactClosed(project, c);
            if (c.Kind is ComponentKind.AuxContactNO or ComponentKind.AuxContactNC or ComponentKind.AuxContactNO_NC or ComponentKind.OverloadNO)
                _auxClosed[c.Id] = IsAuxContactClosed(project, c);
            else if (c.Kind == ComponentKind.ThermalRelayAuxNC)
                _auxClosed[c.Id] = IsAuxContactClosed(project, new ComponentModel { Id = c.Id, Kind = ComponentKind.AuxContactNC, Tag = c.Tag });
            else if (c.Kind == ComponentKind.ThermalRelayAuxNO_NC)
                _auxClosed[c.Id] = IsThermalRelayTripped(project, c.Tag);
            else if (c.Kind == ComponentKind.ConContact)
                _conSwitchedToNo[c.Id] = IsAuxContactClosed(project, c);

            _states[c.Id] = c.Kind switch
            {
                ComponentKind.Fuse => LocalizationService.Get(_blownFuses.Contains(c.Id) ? "Sim_Fuse_Blown" : "Sim_Fuse_Ok"),
                ComponentKind.TimerOnContactNO or ComponentKind.TimerOffContactNO => LocalizationService.Get(_auxClosed.GetValueOrDefault(c.Id) ? "Sim_Aux_NO_Closed" : "Sim_Aux_NO_Open"),
                ComponentKind.TimerOnContactNC or ComponentKind.TimerOffContactNC => LocalizationService.Get(_auxClosed.GetValueOrDefault(c.Id) ? "Sim_Aux_NC_Closed" : "Sim_Aux_NC_Open"),
                ComponentKind.TimerOnOffContactNO_NC => LocalizationService.Get(_auxClosed.GetValueOrDefault(c.Id) ? "Sim_Aux_NONC_Closed" : "Sim_Aux_NONC_Open"),
                ComponentKind.TimerOnDelay or ComponentKind.TimerOffDelay or ComponentKind.TimerOnOffDelay => GetTimerRemainingSeconds(c) > 0.05 ? LocalizationService.Get("Sim_Timer_Counting", GetTimerRemainingSeconds(c)) : LocalizationService.Get(IsTimerOutputActive(c.Id) ? "Sim_Timer_Active" : "Sim_Timer_Idle"),
                ComponentKind.LimitSwitchNO => LocalizationService.Get(_switchClosed.GetValueOrDefault(c.Id) ? "Sim_Limit_NO_On" : "Sim_Limit_NO_Off"),
                ComponentKind.LimitSwitchNC => LocalizationService.Get(_switchClosed.GetValueOrDefault(c.Id) ? "Sim_Limit_NC_On" : "Sim_Limit_NC_Off"),
                ComponentKind.PushButtonNO => LocalizationService.Get(_pressedButtons.GetValueOrDefault(c.Id) ? "Sim_Btn_NO_Pressed" : "Sim_Btn_NO_Released"),
                ComponentKind.PushButtonNC => LocalizationService.Get(_pressedButtons.GetValueOrDefault(c.Id) ? "Sim_Btn_NC_Pressed" : "Sim_Btn_NC_Released"),
                ComponentKind.PushButtonNO_NC => LocalizationService.Get(_pressedButtons.GetValueOrDefault(c.Id) ? "Sim_Btn_NONC_Pressed" : "Sim_Btn_NONC_Released"),
                ComponentKind.PushButtonCon => LocalizationService.Get(_pressedButtons.GetValueOrDefault(c.Id) ? "Sim_Btn_Con_Pressed" : "Sim_Btn_Con_Released"),
                ComponentKind.ToggleSwitchNO => LocalizationService.Get(_switchClosed.GetValueOrDefault(c.Id) ? "Sim_Toggle_NO_Closed" : "Sim_Toggle_NO_Open"),
                ComponentKind.ToggleSwitchNC => LocalizationService.Get(_switchClosed.GetValueOrDefault(c.Id) ? "Sim_Toggle_NC_Closed" : "Sim_Toggle_NC_Open"),
                ComponentKind.ToggleSwitchNO_NC => LocalizationService.Get(_switchClosed.GetValueOrDefault(c.Id) ? "Sim_Toggle_NONC_NO" : "Sim_Toggle_NONC_NC"),
                ComponentKind.ToggleSwitchCon => LocalizationService.Get(_switchClosed.GetValueOrDefault(c.Id) ? "Sim_Toggle_Con_NO" : "Sim_Toggle_Con_NC"),
                ComponentKind.CrossoverSwitch => LocalizationService.Get(_switchClosed.GetValueOrDefault(c.Id) ? "Sim_Crossover_Crossed" : "Sim_Crossover_Direct"),
                ComponentKind.AuxContactNO => LocalizationService.Get(_auxClosed.GetValueOrDefault(c.Id) ? "Sim_Aux_NO_Closed" : "Sim_Aux_NO_Open"),
                ComponentKind.ConContact => LocalizationService.Get(_conSwitchedToNo.GetValueOrDefault(c.Id) ? "Sim_Con_Coil_On" : "Sim_Con_Coil_Off"),
                ComponentKind.AuxContactNC => LocalizationService.Get(_auxClosed.GetValueOrDefault(c.Id) ? "Sim_Aux_NC_Closed" : "Sim_Aux_NC_Open"),
                ComponentKind.AuxContactNO_NC => LocalizationService.Get(_auxClosed.GetValueOrDefault(c.Id) ? "Sim_Aux_NONC_Closed" : "Sim_Aux_NONC_Open"),
                ComponentKind.OverloadNC => LocalizationService.Get(_overloadTripped.GetValueOrDefault(c.Id) ? "Sim_Overload_Tripped" : "Sim_Overload_Normal"),
                ComponentKind.OverloadNO => LocalizationService.Get(_auxClosed.GetValueOrDefault(c.Id) ? "Sim_OverloadNO_Closed" : "Sim_OverloadNO_Open"),
                ComponentKind.ThermalRelayAuxNC => LocalizationService.Get(_auxClosed.GetValueOrDefault(c.Id) ? "Sim_ThermalNC_Closed" : "Sim_ThermalNC_Open"),
                ComponentKind.ThermalRelayAuxNO_NC => LocalizationService.Get(_auxClosed.GetValueOrDefault(c.Id) ? "Sim_ThermalNONC_Tripped" : "Sim_ThermalNONC_Normal"),
                ComponentKind.ThermalRelay => LocalizationService.Get(_thermalRelayTripped.GetValueOrDefault(c.Id) ? "Sim_Overload_Tripped" : "Sim_ThermalRelay_Normal"),
                ComponentKind.Contactor2P or ComponentKind.Contactor3P or ComponentKind.Contactor4P => LocalizationService.Get(IsContactorClosed(c.Id, project) ? "Sim_Contactor_On" : "Sim_Contactor_Wait"),
                ComponentKind.CircuitBreaker => LocalizationService.Get(_protectiveClosed.GetValueOrDefault(c.Id) ? "Sim_Breaker_On" : "Sim_Breaker_Off"),
                ComponentKind.DifferentialBreaker => LocalizationService.Get(_protectiveClosed.GetValueOrDefault(c.Id) ? "Sim_Breaker_On" : "Sim_Rcd_Off"),
                ComponentKind.Breaker1P => LocalizationService.Get(_protectiveClosed.GetValueOrDefault(c.Id) ? "Sim_Breaker1P_On" : "Sim_Breaker1P_Off"),
                ComponentKind.Breaker1PN or ComponentKind.Breaker2P or ComponentKind.Breaker3P or ComponentKind.Breaker3PN or ComponentKind.Rcd1PN or ComponentKind.Rcd3PN => LocalizationService.Get(_protectiveClosed.GetValueOrDefault(c.Id) ? "Sim_MultiPole_On" : "Sim_MultiPole_Off"),
                ComponentKind.AcSupply or ComponentKind.Supply24V => LocalizationService.Get(_sourceEnabled.GetValueOrDefault(c.Id) ? "Sim_Source_On" : "Sim_Source_Off"),
                ComponentKind.Coil or ComponentKind.Lamp or ComponentKind.BlinkingLamp or ComponentKind.Bell or ComponentKind.Siren or ComponentKind.Motor or ComponentKind.DcMotor or 
                ComponentKind.AcMotor or ComponentKind.AcMotor1P => LocalizationService.Get(IsEnergized(c.Id) ? "Sim_Load_On" : "Sim_Load_Off"),
                _ => LocalizationService.Get(IsEnergized(c.Id) ? "Sim_Generic_On" : "Sim_Generic_Off")
            };
        }
    }

    private static string VoltageKey(Guid id, string terminalId) => $"{id:N}:{terminalId}";

    private static Complex[]? GaussianSolve(Complex[,] a, Complex[] b)
    {
        var n = b.Length;
        var m = (Complex[,])a.Clone(); var rhs = (Complex[])b.Clone();
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++) if (Complex.Abs(m[r, col]) > Complex.Abs(m[pivot, col])) pivot = r;
            if (Complex.Abs(m[pivot, col]) < 1e-18) return null;
            if (pivot != col)
            {
                for (var c = col; c < n; c++) (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
                (rhs[col], rhs[pivot]) = (rhs[pivot], rhs[col]);
            }
            var divisor = m[col, col];
            for (var c = col; c < n; c++) m[col, c] /= divisor;
            rhs[col] /= divisor;
            for (var r = 0; r < n; r++)
            {
                if (r == col) continue;
                var factor = m[r, col]; if (Complex.Abs(factor) < 1e-30) continue;
                for (var c = col; c < n; c++) m[r, c] -= factor * m[col, c];
                rhs[r] -= factor * rhs[col];
            }
        }
        return rhs;
    }

    private sealed record SolveResult(Dictionary<TerminalNode, Complex> TerminalVoltages, UnionFind Union);
    private readonly record struct TerminalNode(Guid ComponentId, string TerminalId);

    private sealed class UnionFind
    {
        private readonly Dictionary<TerminalNode, TerminalNode> _parent = new();
        private readonly Dictionary<TerminalNode, int> _rank = new();
        public void Add(TerminalNode x) { if (_parent.ContainsKey(x)) return; _parent[x] = x; _rank[x] = 0; }
        public TerminalNode Find(TerminalNode x)
        {
            Add(x);
            if (_parent[x] != x) _parent[x] = Find(_parent[x]);
            return _parent[x];
        }
        public void Union(TerminalNode a, TerminalNode b)
        {
            var x = Find(a); var y = Find(b); if (x == y) return;
            if (_rank[x] < _rank[y]) _parent[x] = y;
            else if (_rank[x] > _rank[y]) _parent[y] = x;
            else { _parent[y] = x; _rank[x]++; }
        }
    }
}