# 02 — Architecture

Runtime architecture of the WinForms engine, grounded in the actual code under `src/dotNet/`. Claims
are cited as `file:member`. For the pet data model this engine *consumes*, see
[03 — Pet XML Format](03-pet-xml-format.md). For the AI-Edition layer bolted on top (which this doc does
**not** cover), see [`handoff.md`](../handoff.md).

> **Reading note.** Line numbers drift as the file changes, so members are cited by name. The engine is
> C# 7.3 targeting .NET Framework 4.8 (see [`handoff.md`](../handoff.md)). The AssemblyName is
> `DesktopPet` but **the running process shows as `eSheep`**.

## 1. The big picture

```
Program.Main                 (entry: mutex, embedded-assembly load, arg parse)
  └─ ProcessIcon             (tray icon + menu host)
  └─ StartUp   "Mainthread"  (controller: owns Xml + Animations + FormPet[16])
        ├─ Xml               (deserialize animations.xml, decode base64, slice sprite sheet, eval expressions)
        ├─ Animations        (in-memory model + the animation STATE MACHINE + sound)
        └─ FormPet × N        (one borderless layered window per visible pet AND per child)
              └─ NativeMethods (Win32 P/Invoke: EnumWindows, GetWindowRect, title-bar info, …)
```

Everything is single-process, single-UI-thread, and event/timer driven. There is no game loop thread;
each pet owns a `System.Windows.Forms.Timer` whose `Tick` advances one animation step.

## 2. Entry point and process model — `Program.cs`

`Program.Main` (`Program.cs:Main`, the `#if PORTABLE` branch — the shipped one):

1. **Single-instance-ish guard.** Two named mutexes, `"eSheep_Running"` and `"eSheep_Running2"`, allow
   **up to two** instances of the app; a third shows *"Only 2 instances are allowed"* and exits
   (`Program.cs` mutex/`mutex2`).
2. **Embedded dependencies.** `NAudio.dll` and `Newtonsoft.Json.dll` are embedded resources loaded via
   `EmbeddedAssembly.Load(...)` and resolved at runtime through
   `AppDomain.CurrentDomain.AssemblyResolve` → `EmbeddedAssembly.Get` (`Program.cs:CurrentDomain_AssemblyResolve`,
   `EmbeddedAssembly.cs`). This is why the portable build ships as a single self-contained `.exe`.
3. **Command-line args** (`Program.cs:Main`): `localxml=<file>`, `webxml=<url>`, `install=yes`. Any of
   these sets `loadExternalXml`, causing `MyData.SetXml(MyData.LoadXML(), "")` to override the default pet.
4. **Tray + controller.** Creates a `ProcessIcon` (`pi.Display()`), then
   `Mainthread = new StartUp(pi)`, then `Application.Run()`.

There are two compile flavours behind `#if PORTABLE`:

- **Portable** — `Program.MyData` is a file-based `LocalData` (settings + current pet stored on disk / in
  `Properties.Settings`). **This is the build that ships** (see [`handoff.md`](../handoff.md) build notes).
- **UWP** — `Program.MyData` is a `LocalData.LocalData` backed by `Windows.Storage`. Secondary; not built
  by this fork.

`Program.IsApplicationInstalled()` compares the startup path to
`%LOCALAPPDATA%\DesktopPet` to decide whether the app is "installed" (enables the auto-start option).

## 3. The controller — `StartUp.cs`

`StartUp` (aliased `Program.Mainthread`) is the per-run brain of the engine.

- **State.** `FormPet[] sheeps` with `MAX_SHEEPS = 16` (`StartUp.cs`), plus one shared `Xml` and one
  shared `Animations` instance. All pets share the same decoded sprite set and the same state-machine
  model — they differ only in position/phase.
- **Construction** (`StartUp.StartUp(ProcessIcon)`): builds `Xml` with an HD scale factor
  (`2^(scale-1)`), builds `Animations`, then `xml.ReadXML()`. **If reading the user XML fails it falls
  back to the embedded default pet** `Properties.Resources.animations` (the esheep64 pet) and re-reads.
  It then sets the tray icon/metadata from the XML `<header>`, and starts a 1-second timer that spawns
  the first sheep.
- **Hot reload.** `Program.MyData.ListenOnXMLChanged` / `ListenOnOptionsChanged` wire `FileSystemWatcher`
  callbacks (`StartUp.XmlFileChanged`, `StartUp.OptionFileChanged`) so dropping in a new pet or changing
  options reloads live (`StartUp.LoadNewXMLFromString`).
- **Spawning a pet** (`StartUp.AddSheep`): `new FormPet(animations, xml)`, copy every decoded frame in
  via `newSheep.AddImage(sprite)` for each `xml.sprites`, then `Show(spriteWidth, spriteHeight)`. A
  subsequent timer tick calls `Play(...)` to place and animate it.
- **Fleet ops.** `KillSheeps`, `KillSheep`, `TopMostSheeps`, `SyncSheeps` operate across the array.

> The AI-Edition additions live in this same class (`SayAll`, `AskAboutScreen`, `EmoteAll`,
> `EmotionAnimations`, `InitAiTriggers`, `ReloadAiSettings`, idle timer). They are **additive** and
> documented in [`handoff.md`](../handoff.md) — out of scope here.

## 4. Loading a pet — `Xml.cs`

`Xml` turns an `animations.xml` string into runtime objects.

- **Deserialize** (`Xml.ReadXML`): `XmlSerializer(typeof(XmlData.RootNode))` reads the XML string from
  `Program.MyData.GetXml()`. On any exception it deserializes the embedded default pet instead and shows
  a message box. XSD validation issues are routed through `Xml.ValidationEventHandler` into the debug log.
- **Decode + slice** (`Xml.ReadImages` → `Xml.BuildSprites`): the base64 `<png>` sprite sheet is decoded
  to a `Bitmap`, then cut into `TilesX × TilesY` equal cells. `spriteWidth/spriteHeight` are the cell
  size; each cell becomes its own `Bitmap` in `Xml.sprites` (list order = row-major, top-left first).
  The 48×48 icon (`<header><icon>`) is decoded into `Xml.bitmapIcon`. **HD scaling:** frames are
  nearest-neighbour upscaled by `iScale`, but capped so a cell never exceeds 255 px (an `ImageList`
  limit) (`Xml.ReadImages` `while (... > 255) iScale--`).
- **Populate the model** (`Xml.LoadAnimations(Animations)`): iterates the deserialized nodes and fills
  the `Animations` dictionaries. Key detail — **four animation *names* are special** and are captured by
  name into fields used by the engine's lifecycle:

  | `<name>` | Field set | Used when |
  |----------|-----------|-----------|
  | `fall`   | `Animations.AnimationFall` | pet released after a drag |
  | `drag`   | `Animations.AnimationDrag` | user picks the pet up |
  | `kill`   | `Animations.AnimationKill` | app closing (death animation) |
  | `sync`   | `Animations.AnimationSync` | "sync all pets" (About-box cancel) |

  It also translates each `<next only="…">` string into the `TNextAnimation.TOnly` bit flag.

### 4.1 The expression language — `Xml.ParseValue`

XML coordinates/intervals are **strings evaluated as arithmetic expressions**, not just integers. This
is the mechanism that lets one pet definition adapt to any screen size. `Xml.ParseValue` does string
substitution then evaluates with `DataTable.Compute` (so `+ - * / ( )` and precedence all work):

| Token | Replaced with |
|-------|---------------|
| `screenW` / `screenH` | monitor `Bounds.Width` / `Height` |
| `areaW` | working-area width |
| `areaH` | `WorkingArea.Height + WorkingArea.Y` (i.e. the y of the taskbar top / usable bottom) |
| `imageW` / `imageH` | sprite cell width / height |
| `imageX` / `imageY` | parent pet's left/top (used to place **children** relative to the parent) |
| `random` | fresh random 0–99 **every evaluation** |
| `randS` | random 0–99 **fixed until the next spawn** (`iRandomSpawn`, chosen in the `Xml` ctor) |
| `scale` | current HD scale factor |

Whether an expression must be re-evaluated is precomputed into `TValue.IsDynamic` (contains `random`,
`randS`, `imageX`, or `imageY`) and `TValue.IsScreen` (contains `screen` or `area`) in
`Xml.GetXMLCompute`. When flipping a child to the parent's other side, `ParseValue` rewrites `imageW`
sign handling if the parent is flipped.

## 5. The data model &amp; state machine — `Animations.cs`

`Animations` holds the whole pet as dictionaries keyed by integer id:

- `SheepAnimations : Dictionary<int, TAnimation>`
- `SheepSpawn : Dictionary<int, TSpawn>`
- `SheepChild : Dictionary<int, List<TChild>>` (one animation id can trigger several children)
- `SheepSound : Dictionary<int, TSound>` (keyed by the animation id the sound plays with)

The core structs (all in `Animations.cs`):

- **`TAnimation`** — `Start`/`End` movement (`TMovement`), `Sequence` (`TSequence`), and **three separate
  transition lists**: `EndAnimation` (sequence finished), `EndBorder` (hit a border), `EndGravity`
  (nothing underneath). Flags `Border`/`Gravity` say whether those lists exist.
- **`TMovement`** — `X`, `Y`, `Interval` (each a `TValue`, i.e. possibly a dynamic expression),
  `OffsetY`, `Opacity`. **X/Y are per-step pixel *velocities*, not absolute positions.**
- **`TSequence`** — `Frames` (list of sprite indices), `Repeat`, `RepeatFrom`, `Action`, and a
  precomputed `TotalSteps`.
- **`TNextAnimation`** — target `ID`, `Probability` (a relative weight), and `only` (a `TOnly` flag).
- **`TValue`** — `GetValue()` returns the cached int, or re-evaluates the expression if dynamic/screen.

### 5.1 How "what happens next" is chosen — `SetNextGeneralAnimation`

All three of `SetNextBorderAnimation`, `SetNextSequenceAnimation`, `SetNextGravityAnimation` funnel into
the private `Animations.SetNextGeneralAnimation(list, where)`:

1. Sum the `Probability` of every entry whose `only` flag is compatible with the current situation
   `where`. The compatibility test is bitwise: an entry is **skipped** if
   `anim.only != NONE && (anim.only & where) == 0`. `TOnly.NONE == 0x7F` therefore matches every
   situation, so `only="none"` entries are always eligible.
2. Pick a random number in `[1, sum]` and walk the cumulative weights to select an id.
3. Re-evaluate that animation's dynamic values (`UpdateAnimationValues`) and, if the chosen id has a
   registered sound, roll `Probability` and play it via `TSound.Play` (`SheepSound[id].Play`).
4. **If the eligible list is empty, return `-1`** — which the caller (`FormPet`) treats as *"respawn"*.

This is the single most important behavioural rule of the format: **an animation with no applicable
`next` for a situation causes the pet to respawn.** (`TNextAnimation` XML-doc remarks and
`SetNextGeneralAnimation` both state this.)

The `TOnly` flags (`Animations.cs`): `NONE=0x7F`, `TASKBAR=0x01`, `WINDOW=0x02`, `HORIZONTAL=0x04`,
`HORIZONTAL_=0x06` (= horizontal **or** window), `VERTICAL=0x08`.

### 5.2 Spawning — `GetRandomSpawn`

`Animations.GetRandomSpawn` does a probability-weighted pick over `SheepSpawn` to choose the pet's entry
position and its first animation (`TSpawn.Next`). If there are no spawns it falls back to a default at
(0,0) running the first animation.

## 6. The pet window &amp; the animation loop — `FormPet.cs`

Each visible pet (and each child) is one `FormPet` — a borderless, transparent, always-on-top WinForms
form containing a single `pictureBox1` that fills it and an `imageList1` holding every frame.

### 6.1 Rendering: a magenta-keyed layered tool window

- The form is `FormBorderStyle.None`, `ShowInTaskbar = false`, with `BackColor = Magenta` and
  **`TransparencyKey = Magenta`** (`FormPet.Designer.cs`). Any magenta (`#FF00FF`) pixel in the sprite is
  therefore not drawn — this is the transparency mechanism. The XML `<transparency>` element documents
  the key (default Magenta), matching this.
- Extended window styles are set in `FormPet.CreateParams`:
  `WS_EX_TOOLWINDOW (0x80)` removes it from Alt-Tab, `WS_EX_TOPMOST (0x08)` keeps it above other windows,
  `WS_EX_LAYERED (0x80000)` speeds up painting. Children additionally get `WS_EX_NOACTIVATE (0x8000000)`
  so spawning one never steals focus. `FormPet.ShowWithoutActivation` returns `true` for the same reason.
- `Show(w,h)` sizes the form/picture box and finds the taskbar thumbnail window
  (`FindWindowEx("TaskListThumbnailWnd")`) used later for taskbar interaction. `AddImage(Image)` appends
  a frame to the `ImageList`.

### 6.2 The loop: `Play` → `Timer1_Tick` → `NextStep`

- **`Play(first, forceSpawn)`** initialises a spawn: pick a `TSpawn`, set `Top/Left` from its
  expressions (mirrored horizontally if `!IsMovingLeft`), reset `PositionX/PositionY` (float accumulators
  the engine integrates into `Left/Top`), `SetNewAnimation(spawn.Next)`, make the form visible, enable
  `timer1`. On multi-monitor, a random `DisplayIndex` is chosen.
- **`SetNewAnimation(id)`**: if `id < 0` → `Play` (respawn). Otherwise load `CurrentAnimation`,
  `UpdateValues(DisplayIndex)`, spawn any children the id declares (see §7), and set `timer1.Interval`
  to the animation's start interval. If `id == AnimationKill`, no further animation is set.
- **`Timer1_Tick`**: disables the timer, calls `NextStep()`, increments `AnimationStep`, re-enables. Any
  exception surfaces a "Fatal Error" dialog with the current animation dump.
- **`NextStep`** — the heart of the engine. Per tick it:
  1. Picks the frame for `AnimationStep` (honouring `RepeatFrom`/`Repeat` via a modulo index).
  2. Interpolates `interval`, `Opacity`, `OffsetY`, and the per-step velocity `x,y` linearly from
     `Start` to `End` across `TotalSteps`.
  3. If dragging, snaps the pet to the cursor and returns.
  4. **Horizontal border checks** (when `x<0`/`x>0`): against the current window rect if standing on a
     window (`hwndWindow`), else against the screen working area (after `CheckFullScreen`). A hit calls
     `SetNextBorderAnimation(..., VERTICAL or WINDOW)`; if that yields no animation the pet is flagged
     `bLeavingScreen`.
  5. **Downward checks** (`y>0`): taskbar bottom → `SetNextBorderAnimation(..., TASKBAR)`; otherwise
     `FallDetect` to see if a window title bar is in the fall path → `... WINDOW`.
  6. **Upward check** (`y<0`): top of working area → `... HORIZONTAL`.
  7. **Sequence over** (`AnimationStep >= TotalSteps`): if `Action=="flip"`, mirror every frame and
     toggle `IsMovingLeft`; then `SetNextSequenceAnimation` (or respawn if the pet has wandered off
     screen). If the pet is a child with no next, it closes; the `kill` animation fades opacity to 0
     then `Close()`.
  8. **Gravity** (animation has `<gravity>` and pet not over a window with >3px of empty space beneath):
     `SetNextGravityAnimation`. If it *is* on a window, `CheckTopWindow`/`FollowWindow` keep it glued to
     that window (see §6.3).
  9. Integrate `PositionX/Y += x/y`, then either clip the sprite (if leaving the screen edge, so half a
     pet doesn't appear on an adjacent monitor) or set `Left/Top`.

### 6.3 Physics via Win32 — the `NativeMethods` P/Invokes

`FormPet.NativeMethods` (bottom of `FormPet.cs`) is the entire physics toolkit: `EnumWindows`,
`GetWindowRect`, `IsWindowVisible`, `GetWindowText`, `GetTitleBarInfo`, `GetTopWindow`, `GetWindow`,
`GetForegroundWindow`, `SetForegroundWindow`, `ShowWindow`, `FindWindowEx`, plus the `RECT` and
`TITLEBARINFO` structs.

- **`FallDetect(y)`** — `EnumWindows` collects visible, **titled** windows that have a **visible title
  bar** (`GetTitleBarInfo`, skipping the `0x8000` "invisible" state). For each, if the pet is directly
  above the window's top edge, will cross it this step, overlaps it horizontally, and is >20px below the
  screen top, that window becomes `hwndWindow` and the pet lands on `rct.Top`. `CheckTopWindow(false)`
  first confirms the window isn't covered by another window over the pet. Optionally the window is raised
  (`ShowWindow`+`SetForegroundWindow`) if the "bring window to foreground" option is on.
- **`CheckTopWindow(bCheck)`** — walks the Z-order from `GetTopWindow` via `GetWindow(...,2)` to decide
  whether the target window is still the topmost titled window under the pet (i.e. the pet isn't standing
  on something that got buried).
- **`FollowWindow()`** — when the window the pet stands on is moved/resized, translate (and horizontally
  rescale) the pet so it rides along. `NextStep` runs this in a short 16 ms poll loop while the window is
  moving.
- **`CheckFullScreen()`** — if the foreground window covers the whole monitor (a video/game),
  `TopMost` is dropped so the pet hides behind it; restored when the full-screen window goes away.

### 6.4 Interaction — mouse &amp; drag

`FormPet.PictureBox1_MouseDown`: **left-press picks the pet up** (`IsDragging = true`, plays the `drag`
animation); on `MouseUp` it drops and plays `fall`. **Double right-click** closes a single pet
(`pictureBox1_DoubleClick`). Right-click otherwise shows a greeting bubble (AI-Edition change) or, if the
app was started with Shift held, the debug menu. `Form2_DragEnter`/`DragDrop` accept a dropped
`animations.xml` to hot-swap the pet.

## 7. Multiple pets &amp; children

- **Multiple top-level pets:** `StartUp` holds up to `MAX_SHEEPS = 16` independent `FormPet`s, added via
  `AddSheep`/the tray menu; the changelog notes up to 16 pets can auto-start (`Changelog.txt` 1.0.6).
- **Children:** any animation id listed in `<childs>` spawns one or more child `FormPet`s **when that
  animation plays** (`FormPet.SetNewAnimation` → `Animations.HasAnimationChild`/`GetAnimationChild` →
  `child.PlayChild`). Children share the parent's `ImageList`, are positioned relative to the parent
  (via `imageX`/`imageY`), are named `child1..child5`, **cannot be dragged**, and **auto-close when their
  sequence ends** (children have no spawn). Nesting is capped at **5 levels**
  (`FormPet.SetNewAnimation`, `int.Parse(Name.Substring(5)) < 5`). Children are how the sheep interacts
  with a second sprite (a mate, flowers, a bath, etc.).

## 8. Audio — NAudio

`Animations.TSound` wraps NAudio: `Load(byte[])` builds an `Mp3FileReader` + `WaveOut`; `Play(loop)`
plays if `MyData.GetVolume() > 0`, seeking to start each time and re-playing on `PlaybackStopped` for
loops. Sounds are keyed by animation id and fired from `SetNextGeneralAnimation` with a probability roll
(§5.1). MP3 bytes come from base64 in `<sounds><sound><base64>` (a leading `;base64,` data-URI prefix is
stripped in `Animations.AddSound`). If audio fails, volume is forced to 0 and the error is recorded in
`ErrorMessages.AudioErrorMessage`.

## 9. Settings, tray, and lifecycle

- **Settings** live in `Program.MyData` (`LocalData` / `Properties.Settings` for the portable build):
  current pet XML + decoded images + icon, volume, HD scale, multiscreen on/off, "bring window to
  foreground", "steal taskbar focus", autostart. Changes are watched and hot-applied (§3).
- **Tray icon &amp; menu:** `ProcessIcon.cs` owns the `NotifyIcon`; `ContextMenus.cs` builds the menu
  (add pet, options, kill all, about, and — in this fork — "Ask about my screen"). `ProcessIcon.SetIcon`
  is fed the decoded `bitmapIcon` and the `<header>` metadata.
- **Debug:** start the app with **Shift** held to open `FormDebug`, which streams
  `StartUp.AddDebugInfo(type, text)` messages (the info/warning/error log threaded throughout the engine).

## Cross-references

- Pet data model these classes consume → [03 — Pet XML Format](03-pet-xml-format.md).
- Where the sheep, the format, and the fork come from → [01 — History &amp; Lineage](01-history-and-lineage.md).
- Terminology (spawn, child, `only`, border/gravity, sync…) → [05 — Glossary &amp; FAQ](05-glossary-and-faq.md).
