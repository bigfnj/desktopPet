# Legacy build flavors (quarantined)

These are the **dead / non-shipping** build flavors of desktopPet. They were moved
here so they stop cluttering `src/` and can't be opened/built by accident. They are
still tracked in git — nothing is deleted — but **none of them build in this
environment**, and **none of them contain any of the AI-Edition work**.

The product is the **portable** app: `src/DesktopPet_Portable.csproj` -> `DesktopPet.exe`
(build with `..\..\build.ps1`, open `src/DesktopPet_Portable.sln`). All the AI features
(Ollama brain, speech bubble, emotion→animation, AI options tab) compile only there.

## What's in here

| Path | What it is | Why quarantined |
|------|------------|-----------------|
| `DesktopPet.csproj` | The **classic desktop** build → `eSheep.exe`. Uses `packages.config`, references the netstandard `LocalData/LocalData.csproj`. Historically fed the Store package. | `packages.config` won't restore here (no `nuget.exe`; the mixed restore only handles the SDK-style `LocalData`). Does **not** `<Compile>` the `dotNet/Ai/*`, `FormSpeech.cs`, or the AI options tab — so it has none of the AI work. |
| `DesktopPet.sln` | Solution that ties together the classic build + the two UWP projects + `LocalData`. | Opening it drags in the UWP projects (needs a UWP workload) — the exact reason you build the portable `.csproj`/`.sln` directly. |
| `AppWins/` (`OptionsWindow.csproj`) | The **UWP / Windows Store** front end (XAML pages). | UWP workload not installed; the project also carries a stale `D:\GitHub\…` path from the original author's machine. UWP is deprecated (superseded by WinUI 3 / Windows App SDK). |
| `UWPSheep/` (`UWPSheep.wapproj`) | The UWP **packaging** project (MSIX/appx for the Store). | Same — can't build without the UWP workload; ties to `AppWins`. |

## Reviving one of these

The projects use **relative paths that assume they sit in `src/`** (they reference
`dotNet\`, `Portable\`, and `LocalData\` as siblings). The only link that breaks in
this `legacy/` location is the shared `LocalData\` reference.

- **Easiest revive:** move the contents of `src/legacy/` back up into `src/`, restore
  the missing `packages.config` set with a real `nuget.exe`, and (for the UWP pieces)
  install the UWP workload and fix the stale `D:\GitHub\…` path.

## If you want a Store release later

Don't revive the old UWP `OptionsWindow` app. The modern route is to **MSIX-package the
Win32 desktop exe** (Desktop Bridge / Windows App SDK), which packages the portable
`DesktopPet.exe` directly and needs none of the UAP XAML here. Preserving *the ability
to MSIX-package the desktop exe* is what matters; that's cheap to add against a modern
packaging project whenever you want it.
