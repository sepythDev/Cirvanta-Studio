> [!WARNING]
> **Disclaimer (Not Safety-Certified):**  
> Cirvanta Studio is an educational and prototyping tool and is **not a certified electrical engineering or safety simulator**. It uses a simplified steady-state resistive nodal solver and does not model electrical transients, inductance, real motor dynamics, thermal heating, or certified protective-device time/current tripping curves.  
> **Do not rely on this software** for designing real-world electrical installations, safety systems, or protection coordination. Always verify real circuits according to applicable electrical codes and with qualified electrical engineers.
# Cirvanta Studio

A modern electrical schematic editor and real-time circuit simulator built with **C#**, **.NET**, and **Avalonia UI**. Designed for industrial control circuits, motor starters, relay logic, and educational schematics.

---

## Features

### Component Library (Categories & Subcategories)
- **Power Supply:** Single-phase (`L`, `N`, `PE`), Three-phase (`L1`, `L2`, `L3`, `N`, `PE`), and DC (`+`, `−`, `PE`).
- **Protection:** Fuses, 1P / 1P+N / 2P / 3P / 3P+N circuit breakers, thermal overload relays, and 1P+N / 3P+N RCDs.
- **Contacts & Contactors:** Auxiliary contacts (`NO`, `NC`, `NO+NC`, `CON`), timer contacts, thermal relay contacts (`95-96`, `97-98`), and 2P / 3P / 4P contactors.
- **Motors:** 3-phase AC motors (with clockwise / counter-clockwise rotation indicator), 1-phase AC motors, and DC motors.
- **Controls:** Push buttons (`NO`, `NC`, `NO+NC`, `CON`), toggle & crossover switches, and limit switches.
- **Loads & Signaling:** Relay coils, `TON` / `TOF` / `TON+TOF` timers, indicator lamps, blinking lamps, **Bells**, and **Sirens**.

### Real-Time Circuit Simulation
- **AC & DC Nodal Solver:** Simulates 1-phase, 3-phase (120° phase shift), and 24V DC circuits.
- **Animated Current Flow:** Visualizes live current flow direction along energized wires.
- **Interactive Controls:** Press buttons, toggle switches/breakers, trip thermal relays or fuses, and watch coils, contactors, timers, motors, lamps, bells, and sirens respond live.
- **Short-Circuit Detection:** Highlights fault points on the schematic when a direct short occurs.

### Productivity & Interface
- **Drag-and-Drop & Ghost Preview:** Live element preview snapped to the 10px grid while dragging from the palette.
- **Smart Wiring:** Automatic orthogonal wire routing around components, manual segment dragging, wire junctions, and customizable wire colors.
- **Multi-Tab Workspace:** Open multiple schematics in parallel (`+` button) with unsaved changes indicator (`•`).
- **CADe SIMU Import:** Import existing `.cad` files with automatic centering on the canvas, plus native `.cforge` and `.json` support.
- **Auto Day/Night Theme:** Automatically switches between Light and Dark theme based on local time, with manual toggle support.
- **Multilingual UI:** Instant switching between **English**, **Español**, and **Русский**.

---

## Download & Installation

Go to the [**Releases**](https://github.com/sepythDev/Cirvanta-Studio/releases) page and download the build for your operating system (all builds are standalone — no .NET installation required).

### Windows (x64)
1. Download **`Cirvanta.zip`** (unpack it).
2. Double-click **`Cirvanta.exe`** to launch.
   > *Note: If Windows SmartScreen shows a warning ("Windows protected your PC"), click **More info** → **Run anyway** (the binary is unsigned).*

### Linux (x64)
1. Download the **Linux** release file (and extract it if archived).
2. Open a terminal in the folder containing `Cirvanta`, grant execute permission, and run it:
   ```bash
   chmod +x ./Cirvanta
   ./Cirvanta
   
### macOS (Apple Silicon & Intel)

1. Download osx-arm64 (for M1/M2/M3/M4 Macs) or osx-x64 (for Intel Macs) and extract it if archived.

2. Since the app is not notarized with a paid Apple Developer certificate, macOS Gatekeeper may block the first launch. Open Terminal in the folder containing Cirvanta and run:
```bash
chmod +x ./Cirvanta
xattr -d com.apple.quarantine ./Cirvanta
./Cirvanta

---

### Controls & Shortcuts

| Action | Shortcut / Mouse |
|---|---|
| Pan canvas | Hold **Middle Mouse Button** + drag |
| Zoom | **Mouse Wheel** |
| Fit schematic to screen | **Double-click Middle Mouse Button** |
| Rotate selected / preview | **`R`** or toolbar button |
| Undo | **`Ctrl + Z`** or toolbar button |
| Save project | **`Ctrl + S`** |
| Delete selected | **`Delete`** / **`Backspace`** or toolbar button |
| Multi-select | **`Shift + Click`** or drag selection box |
| Edit component tag / timer delay | **Double-click** on component |
| Wire color / Lamp glow / Delete junction | **Right-click** on wire, lamp, or junction |
| Cancel active tool | **`Esc`** or **Right-click** empty space |

---

## Build from Source

Requires **.NET 10 SDK** (or compatible .NET SDK configured in `CircuitForge.csproj`).

```bash
git clone https://github.com/sepythDev/Cirvanta-Studio.git
cd Cirvanta-Studio
dotnet run --project CircuitForge.csproj
