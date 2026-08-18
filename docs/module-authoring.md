# Writing a DesktopPet module

Everything the app can do beyond putting a pet on screen is a **module**: a separate DLL, loaded in its own
collectible `AssemblyLoadContext`, that talks to the app only through a small published contract. Fortunes,
the AI brain and Pet Studio are all modules; the base is a pet engine and a module host.

This is the guide to writing one. It assumes you can build the repo (`pwsh build.ps1 -Release`).

---

## Start here

```powershell
dotnet new install .\templates\desktoppet-module
dotnet new desktoppet-module -n MyThing --moduleId mything --displayName "My Thing" -o modules\MyThing
dotnet build modules\MyThing\MyThing.csproj -c Release
```

That produces a module that already works: a tray item, a settings pane whose values round-trip, a reaction
to the pet being poked, and a self-test. Run `.\packaging\Test-ModuleTemplate.ps1` to see the same thing
scaffolded, built and verified end to end.

Pick the **id** deliberately. It names the output folder, the settings and storage keys, the self-test flag
and the catalog entry, and changing it later orphans the user's settings.

---

## The two assemblies

| | `DesktopPet.Contracts` | `DesktopPet.ModuleKit` |
|---|---|---|
| What | The contract: `IModule`, `IHost`, `IPet`, the DTOs | Convenience helpers |
| Reference as | `Private="false"` | normal (private) |
| At runtime | **one shared copy**, owned by the host | a copy **inside your module's folder** |
| Stability | `AssemblyVersion` frozen at `1.0.0.0`, forever | ordinary library, moves freely |

`Private="false"` on the contract is the single most important line in your csproj. The loader resolves
`DesktopPet.Contracts` from the *default* context, so host and module share one copy and the types unify. Ship
your own copy and every cast to `IModule` fails at load with a message that looks like nonsense.

The reverse is true for ModuleKit: it ships *with* you, so each module can move to a new ModuleKit
independently.

---

## The contract

One file: [`src/DesktopPet.Contracts/PluginApi.cs`](../src/DesktopPet.Contracts/PluginApi.cs). It is
deliberately handle-based (`IPet`, never the app's `FormPet`) and framework-agnostic (no WinForms, WPF or
`System.Drawing`) so it can stay small and stable.

**You implement** `IModule`: `Info`, `Init(IHost)`, `Shutdown()`. Exactly one public class per DLL.

**You consume** `IHost`: pet events (`PetSpawned`, `PetPoked`, `PetLanded`, `HostShutdown`), speech (`Say`,
`SayAll`, `SpeechEnabled`), animation (`TryPlayAnimation`, `PlayAnimationAll`), screen reading
(`CaptureScreenContext`), storage and settings (`GetStorage`, `GetSettings`), input (`RegisterHotkey`,
`RegisterDropResponder`, `RegisterPokeResponder`), content (`FetchCatalogItemsAsync`,
`DownloadCatalogItemAsync`), pets (`GetPetManager` → `IPetManager`), and UI (`AddTrayItems`,
`AddOptionsPane`, `PickFilesToOpen`, `OpenLink`).

### Permissions

Declare only what you use — they are shown to the user *before* install, so an honest list is a feature:

```csharp
Permissions = ModulePermissions.Speech | ModulePermissions.Storage,
```

A service you did not declare hands back a **refusing stand-in** rather than throwing: without
`ModulePermissions.Pets`, `GetPetManager` returns a manager whose every verb returns false with a reason. So
check return values; do not assume success.

### MinHostVersion

```csharp
MinHostVersion = "1.4.6",   // the host that added IPetManager.PetsDirectory
```

Checked **before** `Init`, against the host's *product* version (not the frozen `AssemblyVersion`). Raise it
only when you actually call a member a newer host introduced: a module that demands a host newer than the one
shipped is refused **forever**. Leaving it out means "runs anywhere".

### Rules the host relies on

- **Never throw.** Throw in `Init` and you are skipped with a log line; throw in a tray click and you eat the
  click. Wrap handlers in `try/catch`.
- **Handlers run on the UI thread.** Keep them short. Marshal background work back yourself.
- **Clean up in `Shutdown`.** Timers, windows, hotkey registrations, preview pets. The load context is
  unloaded afterwards.
- **Modules load only at startup.** There is no hot-load; install, update and uninstall all restart the app.

---

## ModuleKit

Reference [`src/DesktopPet.ModuleKit`](../src/DesktopPet.ModuleKit) and stop writing these yourself:

| Type | Use it for |
|---|---|
| `AtomicFile.TryWriteAllText` | A write that survives a crash. Returns false rather than throwing. UTF-8, no BOM. |
| `CrossSessionLock` | Guarding a file against a second session/instance writing at the same time. |
| `JsonSettingsStore<T>` | Structured state a settings pane can't express (lists, nested objects, a schema version). |
| `ModulePaths` | Your data directory, from `IModuleStorage`, with a temp fallback if Storage wasn't declared. |
| `EmbeddedResources` | Reading a file you embedded (icon, seed data), matched on the trailing name. |
| `UnicodeTextProgress` | Advancing or clipping text without splitting a surrogate pair. |
| `SelfTestProbe` | The PASS/FAIL/RESULT report shape the gate parses. |
| `Testing.RecordingHost` + fakes | Driving your module in a self-test with no window, pet or network. |

Two things go in your **data directory** (`ModulePaths`), never beside the exe: anything durable, because a
per-user install directory is read-only-ish and a module *update* replaces the install folder while
deliberately preserving the data directory.

Settings: use the host's `IModuleSettings` (`GetSettings`) for flat keys behind a pane — the host persists it
and encrypts `Secret` fields. Reach for `JsonSettingsStore<T>` only when the shape outgrows that.

---

## Contributing UI

You declare data; the host renders it. That is why a module needs no UI framework.

- **Tray items** — `AddTrayItems`. `Group`/`Order` place them; `Visible`/`DynamicText` are re-evaluated on
  every menu open; `IconPng` is raw PNG bytes (the ABI stays free of `System.Drawing`).
- **A settings pane** — `AddOptionsPane` with a `Schema` of `SettingField`s (`Bool`, `Int`, `Text`, `Enum`,
  `Secret`, `Info`), optional `PaneAction` buttons (the returned string is shown next to the button), optional
  `ListCard`s for checkable lists, and `Load`/`Save` delegates.
- **A window of your own** is possible but exceptional — Pet Studio does it because an authoring canvas isn't
  expressible as a schema. Set `UseWPF`/`UseWindowsForms` and own the window's lifetime.

---

## The self-test

Every module ships one. The host keeps **no compile-time reference** to any module, so it reaches yours by
reflection across the load-context boundary: a `public static bool SelfTest(out string detail)`.

```csharp
public static bool SelfTest(out string detail)
{
    var probe = new SelfTestProbe();
    var host = new RecordingHost();
    using (var storage = new TempModuleStorage("mything"))
    {
        host.UseStorage("mything", storage);
        var module = new MyThing();
        module.Init(host);
        probe.Check("contributes a tray item", host.TrayItems.Count == 1);
        host.RaisePetPoked(new PokeInfo());
        probe.Check("reacts to a poke", host.SaidLines.Count == 1);
        module.Shutdown();
    }
    return probe.Finish(out detail);
}
```

Wire it up in three places, or it does not run:

1. a `--mything-selftest` flag in [`src/dotNet/Program.cs`](../src/dotNet/Program.cs),
2. the `$flags` map in [`tests/run-gate.ps1`](../tests/run-gate.ps1),
3. the flag list in [`.github/workflows/build.yml`](../.github/workflows/build.yml).

**Never skip silently.** The gate fails on a `SKIP:` line on purpose: a self-test that skips reads exactly
like one that passed, and that has hidden a real bug here before.

---

## Publishing

`modules-dist/` is served straight off `raw.githubusercontent.com/.../master/`, so **merging to master IS the
publish** — no tag, no release, no upload. Existing users see it on their next catalog check.

One command:

```powershell
.\packaging\New-ModulePublish.ps1 -ModuleId mything -Name "My Thing" -Description "What it does." -Commit
```

It builds, zips into `modules-dist/mything.zip`, registers the entry in `modules-dist/modules.json` (reading
the version and permissions out of your source so they can't drift), commits, regenerates `catalog.json`, and
runs the freshness check. Then commit `catalog.json` and merge.

Two traps it exists to prevent, both of which have shipped bugs:

- **`catalog.json` hashes the COMMITTED git blob**, because that is what raw.githubusercontent serves. Zip →
  **commit** → catalog. Get that order wrong and the catalog advertises a hash nobody can download. The script
  refuses to continue while the zip is uncommitted.
- **`modules.json`'s version is what the in-app Update button compares against.** Lag your
  `ModuleInfo.Version` and the update is never offered; lead it and it is offered forever, surviving every
  install.

[`packaging/Test-ModulePublishFreshness.ps1`](../packaging/Test-ModulePublishFreshness.ps1) fails CI if a
module's source is newer than its published zip, or if the three versions disagree. Practically: **a PR that
touches `modules/<Id>/` needs a republish commit.**

---

## Gotchas

- **`GenerateDependencyFile` + `CopyLocalLockFileAssemblies`** are required the moment you reference anything
  (including ModuleKit): a library project otherwise leaves its dependencies out of the output and the load
  context can't resolve them.
- **Native NuGet assets** (onnxruntime) flatten *beside* your DLL rather than under `runtimes\win-x64\native`,
  which is why the loader also probes your module's own folder. Pin `RuntimeIdentifier=win-x64` with
  `SelfContained=false`.
- **`net10.0-windows` in-box:** `System.Text.Json`, DPAPI, `System.Drawing`, ConfigurationManager. Adding one
  as a `PackageReference` trips NU1510, which is an **error** here (warnings-as-errors).
- **WinRT pins your load context** so the module never unloads — tolerable only because uninstalling already
  forces a restart.
- **`AppendTargetFrameworkToOutputPath=false`**, or your DLL lands in a TFM subfolder the loader never looks in.
- **A loaded DLL cannot be deleted by the process that loaded it.** That is why uninstall and update are
  deferred to the next launch. Don't fight it.
- **Don't declare an event you never raise.** Two ABI events were removed at the freeze for exactly that: a
  silent event in a shipped contract is a trap.

---

## Where things live

| | |
|---|---|
| The contract | `src/DesktopPet.Contracts/PluginApi.cs` |
| ModuleKit | `src/DesktopPet.ModuleKit/` |
| The loader | `src/dotNet/Plugins/ModuleHost.cs` |
| The host bridge (real `IHost`) | `src/dotNet/Plugins/PetHost.cs` |
| Existing modules | `modules/Fortunes`, `modules/AiBrain`, `modules/PetStudio`, `modules/TestModule` |
| Module self-tests | `src/dotNet/Plugins/*ModuleSelfTest.cs` |
| Template | `templates/desktoppet-module/` |
| Packaging | `packaging/New-ModulePublish.ps1` |
| The gate | `tests/run-gate.ps1` |

Third-party modules (signing, consent, a marketplace, building outside this repo) are a planned later phase:
see [`module-ecosystem-roadmap.md`](module-ecosystem-roadmap.md).
