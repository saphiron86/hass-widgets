# HASS Widgets

Small desktop widgets for Windows that show Home Assistant entities.

- Single portable exe (`dist\HassWidgets.exe`, about 55 KB). Nothing to install: it uses the .NET Framework 4.8 that is part of Windows 10 and 11.
- Settings live in a `config` folder next to the exe (`config\settings.json`). Copy the exe and that folder to move everything.
- Talks to Home Assistant over its REST API with a long-lived access token.

## Using it

1. Run `HassWidgets.exe`. On first start it asks for the server URL (for example `http://homeassistant.local:8123`) and a token. Create the token in Home Assistant under your profile: Security > Long-lived access tokens.
2. Pick an entity and a display mode. The app sits in the system tray; right-click the tray icon to add more widgets.

| Action | How |
| --- | --- |
| Move | Drag the widget. It snaps to the edges of the display and to other widgets; hold Shift while you click and drag to move freely. |
| Resize / scale | Drag any edge or corner, or Ctrl + mouse wheel. Content scales with the window. |
| Configure | Right-click the widget > Configure... |
| Bring widgets to the front | Double-click the tray icon |

Per widget: entity, title, display mode (number, line graph, bar chart, gauge), update interval, graph time span, decimals, scale min/max, unit, background / title / entity name / value / accent colour, opacity, always on top, lock.

Position and size are saved automatically and restored on the next start.

A widget can show several entities: pick a device, then tick the entities you want. Number mode lists them one per line, graphs stack one above the other, and gauges sit in a grid. They appear in the order you ticked them. Type a short name in the "Display name" column next to an entity to replace its Home Assistant name in the widget.

## Notes

- Line and bar widgets load their history from the Home Assistant recorder, then extend it with each poll. `on` / `off` states are graphed as 1 / 0.
- The gauge runs from 0 to 100 unless you set a scale minimum and maximum.
- The token is stored encrypted with Windows DPAPI, tied to your Windows account on this PC. If you copy the folder to another PC or account, the widgets and settings come along but the app asks for the token again.
- To start with Windows, put a shortcut to the exe in the folder that `shell:startup` opens.

## Building

Run `build.cmd`. It uses the C# compiler included with Windows, so no SDK is needed. That compiler is C# 5, so the source avoids newer language features.

## Licence

MIT. See [LICENSE](LICENSE).
