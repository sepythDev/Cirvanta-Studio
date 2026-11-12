# Cirvanta Studio

**Electrical Design & Simulation**

Port of the WPF MVP 0.5.0 to Avalonia 11.3 and .NET 10. Runs on Windows, Linux, and macOS.

1. Copy `power-lightning.png` from the old project's `Assets` folder into `Assets/`.
2. Run `dotnet run`.

Unhandled exception log: `%LOCALAPPDATA%\CircuitForge\crash.log` (Linux/macOS: `~/.local/share/CircuitForge/crash.log`).

## Separate WPF Version

The WPF version is located in `Wpf/` and is built separately from the Avalonia version:

```powershell
dotnet run --project Wpf/CircuitForge.Wpf.csproj
```

It uses shared models, services, and the simulator from the project folders, while keeping its own windows and resources in `Wpf/`.

Controls: mouse wheel — zoom, middle mouse button — pan, drag from the palette onto the workspace, R — rotate, Delete/Backspace — delete, Esc — cancel, Ctrl+Z — undo, Ctrl+S — save, double-click an element — edit its designation, right-click a wire or lamp — change its color.
