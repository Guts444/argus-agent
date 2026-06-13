Argus v0.3.1 — Hotfix

Fixes a WinUI 3 binding crash (`STOWED_EXCEPTION` in `CoreMessagingXP.dll`)
that occurred on the project cockpit dashboard a few seconds after launch.

**What was fixed**

- All `<Run Text="{Binding ...}"/>` elements in the Project Cockpit replaced
  with safe `TextBlock.Text` bindings. WinUI 3's binding engine on `Run`
  (a `TextElement` subclass, not `FrameworkElement`) can throw stowed
  exceptions on indexed collection paths like `NextActions[0]` and on
  rapid ObservableCollection resets.
- Computed display properties added to `ProjectDashboardCard` and
  `CoherentDashboard` records (`OpenTaskCountText`, `FirstNextAction`,
  `HasGlobalBlockers`, etc.) so the XAML never binds to collection
  indexers or uses inline `<Run>` text assembly.
- `BoolToVisibilityConverter` (which always collapsed the header stats)
  replaced with `NullToVisibilityConverter` for the `ProjectCockpit`
  binding.

**Install**

```
irm https://raw.githubusercontent.com/Guts444/argus-agent/main/scripts/install.ps1 | iex
```

Or download `ArgusAgentSetup-x64.exe` from this release. SHA-256
checksum is published alongside the installer.
