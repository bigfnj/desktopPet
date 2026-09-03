# DesktopPet.ModuleKit

Optional helpers for writing a [Desktop AI Companion](https://github.com/bigfnj/desktopPet) module. The
required reference is [`DesktopPet.Contracts`](https://www.nuget.org/packages/DesktopPet.Contracts); this is
the code the first-party modules kept re-copying by hand until it was collected here.

| Type | For |
|---|---|
| `AtomicFile.TryWriteAllText` | A write that survives a crash. Returns false rather than throwing. UTF-8, no BOM. |
| `CrossSessionLock` | Guarding a file against a second session or instance writing at the same time. |
| `JsonSettingsStore<T>` | Structured state a settings pane can't express — lists, nested objects, a schema version. |
| `ModulePaths` | Your data directory, from the host, with a temp fallback if you didn't declare Storage. |
| `EmbeddedResources` | Reading a file you embedded, matched on the trailing name so a namespace rename can't break it. |
| `UnicodeTextProgress` | Advancing or clipping text without splitting a surrogate pair. |
| `SelfTestProbe` | The PASS/FAIL/RESULT report shape the app's gate understands. |

## Testing a module with no app running

`DesktopPet.ModuleKit.Testing` has a headless `IHost`, so a module's behaviour is ordinary unit-testable:

```csharp
var host = new RecordingHost { IsDarkTheme = true };
using (var storage = new TempModuleStorage("mything"))
{
    host.UseStorage("mything", storage);
    var module = new MyThing();
    module.Init(host);

    host.RaisePetPoked(new PokeInfo());
    Assert(host.SaidLines.Count == 1);
    module.Shutdown();
}
```

It records tray items, settings panes, speech, logged lines, played animations and registered responders;
raises the pet lifecycle events; hands out fake settings and a temp data directory; and arbitrates
drop/poke responders in registration order the way the real host does. `DenyingPetManager` lets you prove
your module degrades gracefully when it lacks a permission.

Pair it with the app's `--module-selftest=<id>` flag, which loads your module through the *real* loader and
calls its `public static bool SelfTest(out string detail)`.

## Versioning

This ships **inside** your module's folder rather than being shared by the host, so each module carries its
own copy and two modules may use different versions. That is why these helpers live here and not in the
frozen contract.

Full guide: [`docs/module-authoring.md`](https://github.com/bigfnj/desktopPet/blob/master/docs/module-authoring.md).
