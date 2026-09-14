# 7 Days to Die — Save Transfer

Carries 7 Days to Die saves between PCs on a USB stick, for people who should not have to think
about it. The EXE lives on the stick; you plug it in, run it, and press the orange button.

7 Days to Die has **no Steam Cloud support** (verified: no `Steam/userdata/<id>/251570` is ever
created), so saves are entirely local and there is no first-party answer to this.

## Download

Grab the latest from **[Releases](../../releases)**. Two flavours:

| File | Size | Needs |
|---|---|---|
| `SaveSync.exe` | 66 MB | Nothing. One file, double-click it. This is the one for the USB stick. |
| `SaveSync-small.zip` | 249 KB | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime) (x64) |

Windows will say "Windows protected your PC" the first time, because the program is not code-signed:
**More info** then **Run anyway**. Once per PC.

## Build

```
dotnet test src/SaveSync.Core.Tests/SaveSync.Core.Tests.csproj      # 218 tests
dotnet publish src/SaveSync.App/SaveSync.App.csproj -c Release -o dist
```

`dist/` is what goes on the stick: one self-contained `SaveSync.exe` (~63 MB, no .NET runtime
needed on the target PC), plus `READ ME FIRST.txt` and `quick-start.html`.

Do **not** ship a `config.json` in `dist/` — settings are per-PC, and one sitting next to the EXE
would carry one machine's save-folder override to every other machine.

## What it moves

| Path | Why |
|---|---|
| `%APPDATA%/7DaysToDie/Saves/<World>/<Save>/` | The world itself. |
| `%APPDATA%/7DaysToDie/GeneratedWorlds/<World>/` | Only for random-gen worlds. Without it the save will not load — the most common "I copied my save and it's broken". |
| `%APPDATA%/7DaysToDie/Mods/<Mod>/` and `<install>/Mods/<Mod>/` | A save played with mods misbehaves or refuses to load without them, so a save that travels alone is half a transfer. |

Excluded: `logs`, `BacktraceLogs`, `RfsCache`, `RfsPlayerCache`.

Discovery never assumes a path. It checks Steam launch options for `-UserDataFolder`, then the
default AppData location, then beside the install; the install itself is resolved through Steam's
`libraryfolders.vdf` + the app manifest (three libraries on the dev machine), a running game
process, or the uninstall registry keys. Everything is overridable in the UI.

## The design, in one paragraph

A save is a mutable working set with **no merge function** — two divergent copies of a `Region`
folder can never be combined. So this is not a sync tool; it is a baton. Every copy of a save
carries a **passport** (`.savesync.json`) holding a unique version id and an ancestor chain, which
lets any machine decide *offline* whether an incoming copy is newer, older, or divergent. A plain
version counter is enough for two machines and silently wrong for three, which is why ancestry is
used instead and `Ordinal` is cosmetic.

Every write is the same transaction: stage to a scratch dir on the same volume, verify every file
against a SHA-256 manifest, move the current save aside into `trash/`, move the new one in, then
file the old one as a snapshot. **Nothing is ever deleted.** A crash mid-commit is reconstructed on
next launch: if the target slot is empty the parked copy goes back, if it is occupied the parked
copy becomes a backup.

## Mods

Mods travel with the save, under one rule that is stricter than anything applied to saves: **an
existing mod folder is never written over.** A mod folder holds that machine's own settings, and
those are precisely what the sending machine cannot know about. So the outcomes are install (not
here), skip (identical), or keep-yours (here but different) — and "different" includes the case
that breaks naive tools, *same name, same version number, edited config inside*, which is why
identity is a hash over the mod's file manifest rather than the version string.

An absent hash means "not proven identical", which resolves to keep-yours. Every unknown fails
towards leaving the local copy alone.

Discovery walks each `Mods` root up to `MaxDepth` levels looking for `ModInfo.xml`, rather than
assuming the one level the game documents. Mods arrive as zips and get extracted with a wrapper
folder, or two — the single commonest reason a mod "doesn't work" — and a tool that reports "no
mods" on a PC that visibly has them is worse than useless. A mod found at depth 3 is installed at
depth 1 on the far side, so the transfer quietly straightens it out. A folder holding `ModInfo.xml`
is never descended into: some mods ship a disabled variant inside themselves, and the game loads
only the outer one.

Mods are installed **before** the save swap and their failures are contained: a mod that cannot be
read is skipped with a warning and the save still goes. That asymmetry is deliberate — mods are
additive and reinstallable, a save is neither, so a mod problem must never take a save with it.
Conversely a save swapped in *without* its mods is a world that will not load, which is why the
order is mods-then-save rather than the reverse.

Each mod lands under a `.savesync-part` name and is renamed only once the copy is complete, so the
game can never find a half-copied mod. An interrupted copy leaves a folder the sweep recognises and
clears, not a mod that loads and breaks the game.

## Over the network

Same packages, same verification, different pipe. The transport is interchangeable because every
transfer is a package with a manifest; nothing about the safety model changes.

- **Plain TCP, not HTTP.** `HttpListener` needs an admin URL reservation for any non-localhost
  prefix; a `TcpListener` runs as an ordinary user. Framing is a 4-byte length, a JSON header, then
  the body.
- **UDP broadcast discovery** on every live IPv4 network (a PC with a VPN or virtual switch has
  several; guessing one is how discovery silently fails on the machine you cannot test on).
- **Pairing is automatic.** No codes, no prompts. Safe because a peer cannot talk a machine into
  losing anything: incoming bytes land in `.savesync/inbox`, and the *receiving* engine decides
  whether to apply, using the same rules as a USB stick.
- **Auto-applies only when provably safe**, i.e. `NoLocal` or `FastForward` with no unsaved local
  play. Anything else waits in the inbox for a person.
- **The one-time Windows permission** is a firewall rule added via `--setup-network` (elevates
  once). Keyed to the port, not the program path, because the program is meant to live on a USB
  stick and a stick is not always the same drive letter.

`PeerWatcher` polls the other PC every 15s, compares passports, and raises the "they have a newer
copy" news that drives the tray notification. `request-send` asks the far side to package and push;
it only reads over there.

## Driving the other PC

The point of all of the above is to stop anybody having to walk over to a machine. These do the
rest of it, and none of them needs a person at the far end to click anything:

| Ask | What happens there |
|---|---|
| `get-machine` | A full account of that PC: see below |
| `get-log` | That PC's account of what it has been doing |
| `rename-save` | Renames one save. Nothing copied, nothing deleted |
| `restart` | Hands over to the installed copy, so a pushed update takes effect |
| `update-offer` / `update-file` | A newer program, checked against a checksum declared before a byte is sent |
| `game-stop` | Asks the game to close. Asked, never killed - it writes the world on the way out |
| `game-start` | Starts the game, optionally **straight into a named save**, past the spawn screen |
| `spawn-pref-reset` | Hands back the one setting a remote launch borrows |

### Answering "why is it running badly" from another PC

`get-machine` exists because "it runs badly on the laptop" is not something anybody can act on from
another house, and walking over to look is the thing this program exists to avoid. It reports:

- **Which chip the game is actually drawing on**, read from the game's own log — the only
  unambiguous answer on a laptop that has two, and not the same thing as which cards are installed.
- **The graphics card's clock against its maximum, its power draw, its temperature against its own
  backing-off threshold**, and what the driver says is holding it back. A card at 100% is only
  saturated at whatever speed it is currently *allowed* to run.
- **Frame rate over time**, not just an average. A machine that starts fast and slides downwards
  while the world stands still is getting hot; a scene that is genuinely too heavy is slow from the
  first frame and stays there. The average cannot tell those apart and they have opposite answers.
- **Processor use per process**, over a measured window — a different list from the memory one.
- **Whether it is paging**, said plainly either way, because "maybe it's the page file" is a theory
  that survives indefinitely until somebody produces a number.
- **Whether a laptop is on battery**, and the Windows power plan.
- **The game's graphics settings**, as raw numbers rather than invented labels.

This was built to settle one real argument and did: a laptop blamed on its base turned out to be
running its graphics card at 300 MHz of 2100 at 83C, going from 45.8 fps to a flat 8.5 over fifteen
minutes while the player stood still in the same base.

### Starting a named save from another PC

The game supports this itself; it is how it restarts back into your world after a setting change.
Three arguments, read out of the game's own code rather than guessed at:

```
-world=<World> -name=<Save> -LoadSaveGame=true
```

`-LoadSaveGame` is a **bool**, not the name of the save - `LaunchPrefs` parses it with
`bool.TryParse`, and putting the name there yields `Could not parse config value` and a game
sitting at its menu. The bool switches on `Platform.PlatformApplicationManager.LoadSaveGame`, a
state machine that finds that world and save among the ones on the PC and works the menu itself:
`ContinueGameOpen -> ContinueGameSelect -> ContinueGamePlay -> Done`. It narrates that to the log,
so the result is readable afterwards from another machine.

### The last click: the spawn screen

Loading the save is not the same as being in it. The game loads the world and then waits on a
Spawn button, in `GameManager`:

```csharp
if (!GamePrefs.GetBool(EnumGamePrefs.SkipSpawnButton) && !IsEditMode())
{
    canSpawnPlayer = false;
    XUiC_SpawnSelectionWindow.Open(...);
    while (!canSpawnPlayer) yield return null;
}
```

That button cannot be automated - the only auto-press in the game is gated on `AutomationRunner`,
whose script loader logs `Disabled for this build type` in retail builds. But the *gate* is a game
preference, so `-SkipSpawnButton=true` goes on the command line and the world comes up with the
player in it. It affects only this gate; choosing where to respawn after dying is a different
window (`_chooseSpawnPosition: true`) and is untouched.

**That preference persists**, which is the part worth being careful about: it is declared with the
`StandaloneWindows` flag, so the game writes it to the registry on exit and it would stay changed
for every launch afterwards. So it is borrowed, not taken — captured before the launch and put
back once the game closes, including the common case of "it was never set", which restores by
deleting rather than by writing a zero.

The restore is written to `%LOCALAPPDATA%\SaveSync\spawn-pref-restore.json` as well as held in
memory, because this program is updated and restarted from another machine as a matter of routine
and an in-memory restore does not survive that. It did not survive it, once, on a real laptop —
which is also why `spawn-pref-reset` exists. Anything that borrows a setting on somebody else's PC
should ship with the button that gives it back.

Two things follow from how the game behaves, and both are in `GameLauncher`:

- **A name that matches nothing is not an error to the game.** It makes a brand new world under
  that name - `[LoadSaveGame] Creating new save game`. A typo would leave somebody's PC sitting in
  an empty world that looks exactly like a wiped save, so a save that is not there is refused
  before anything starts.
- **The arguments need the game's executable, not `steam://`,** which carries no arguments. That
  means taking over a job the game's own launcher does: turning `launchersettings.json` into flags.
  Getting it wrong would change how somebody's game runs - it could switch EasyAntiCheat back on
  for a person who turned it off. So no launch is invented: the one that machine last used is
  repeated, read out of `logs/launcher.log`, and only a PC that has never started the game falls
  back to deriving one from the settings file.

## Running from anywhere

The program is a single EXE with no installer requirement, and it is expected to be run from a USB
stick, the Desktop, Downloads, Program Files, or a network share — several of which are read-only.
Nothing about where it sits may decide whether it works.

- **Settings fall back.** `AppConfig.SaveTo` writes beside the program when it can and to
  `%LOCALAPPDATA%\SaveSync` when it cannot. Without this, one write-protected stick turns into
  "the program is broken".
- **The newer settings file wins** when one exists in both places. Preferring the beside-EXE copy
  unconditionally is what produces the worst version of this: a read-only stick whose stale settings
  are read forever while every change silently goes somewhere else.
- **The workspace never lives beside the program.** Snapshots and staging go inside the game's
  user-data root, because commit is a same-volume directory move and the program's own folder is on
  whatever drive somebody happened to copy it to.
- **The firewall rule is keyed to the port, not the program path**, since a stick is not always the
  same drive letter.

Verified by running `--diagnose` from a plain folder, a path with spaces and symbols, a 17-level
deep path, and a folder with an explicit deny-write ACL. `--diagnose` reports whether it can write
beside itself, and is read-only: it never creates the folders it reports on.

## Installed vs portable

Both. The stick carries the installer. `Installer.Install()` copies to `%LOCALAPPDATA%/SaveSync/app`
and adds a per-user `Run` entry — no admin needed. Deliberately **not** a Windows service: a service
runs outside the user session, so it cannot reliably reach `%APPDATA%` or show the user anything.

Only an installed copy can do the automatic "the laptop came home with a newer save" detection,
because only an installed copy is running when nobody has opened it.

## Backups

`SnapshotsToKeep` defaults to **0, meaning keep everything**. It was 10, and a count-based cap made
the tool's own printed promise — "nothing is ever deleted" — false for exactly the backup people
want: the one from three transfers ago, noticed late, and the only one that cannot be recreated.
Backups now go only when somebody deletes one in the Backups window.

Every snapshot folder carries `READ ME - how to put this back.txt`, written at commit time. The
`.meta.json` beside the folder is for the tool; the note is for a person opening the folder in
Explorer in a year, possibly with the tool long gone — which is the exact moment a backup has to
still make sense. It therefore also spells out the manual restore, so nothing about recovery
depends on this program existing. The note is excluded from manifests and deleted from a restored
copy, so it never leaks into a live save or a package.

Restoring pins whatever it replaced: the person reaching for an older version is the likeliest one
to want the newer one back five minutes later.

## Safety invariants

Each has a dedicated test in `src/SaveSync.Core.Tests`.

1. Nothing is deleted — replaced saves become restorable snapshots; the losing side of a conflict is pinned.
2. No transfer runs while `7DaysToDie.exe` is running.
3. Every byte is verified against the manifest before anything live is touched.
4. Commit is an atomic same-volume directory move (hence the workspace lives inside the user-data root, not `LOCALAPPDATA`).
5. Older can never silently overwrite newer.
6. Divergence preserves both sides and refuses to guess.
7. A save that exists but has never been registered is `Unregistered`, **not** `NoLocal` — collapsing those would make first-run overwrite real progress.
8. A random-gen world without its map data blocks the import rather than producing an unloadable save.
9. Re-installing a package already applied here is flagged.
10. A save played since it was last packaged blocks an incoming fast-forward. Ancestry cannot see
    uncommitted play - the passport still describes the older version - so without this an incoming
    copy looks clean and silently destroys the session just played. `ImportPlan.LocalPlayedSinceLastCopy`.
11. A peer-supplied file path that is absolute or climbs out of the inbox is refused outright.
12. "Played since last copy" compares a file time against the file time recorded at commit, never
    against a wall clock, and allows a few seconds of slack. Comparing against `CommittedAt` was
    wrong twice (after an import it belongs to the *sending* machine, and two PCs disagree on the
    time); an exact match was wrong again (FAT32 sticks round to two seconds). Each mistake made
    every transfer look freshly played and invented conflicts forever after.
13. Network transfers restore each file's original timestamp. Without it the network path produced
    exactly the false-played bug above on every single transfer.
14. Everything that modifies a save takes `MutationGate`. A transfer can start from three places at
    once - a button, an arriving package, the background watcher - and two swaps of the same folder
    cannot be untangled afterwards.
15. The game-running guard is re-checked immediately before the swap, not just at the start;
    verifying a large save takes long enough for someone to launch the game meanwhile.
16. A mod folder already on this machine is never written over, and a missing hash resolves to
    keep-yours. Two mods sharing a version number but not their settings are *different*.
16b. The losing side of an `Unrelated` take-over is pinned. That folder was never an older version
    of the incoming save — it is a separate game, quite possibly another person's, so it is the one
    backup that must never become prunable. This was missing until a test went looking for it.
17. Nothing this tool creates is deleted on a timer or a count. Only a person deletes a backup.
18. Every backup folder is self-describing, including the manual restore path, so it survives the
    tool itself being gone.
19. A mod failure — unreadable source, corrupt payload, locked target — never blocks or half-applies
    the save transfer. Mods are additive and reinstallable; a save is not.
20. A half-copied mod is never left under a name the game would load.
21. Settings save somewhere, whatever folder the program was run from. A read-only location
    downgrades where settings live, never whether the program works.
22. Mods that shipped with the game never travel between PCs. Each install has its own copy, matched
    to its own game version, and overwriting it is a way to break a working game.

## Layout

```
src/SaveSync.Core/        engine: passports, ancestry, manifests, snapshots, planning
src/SaveSync.Core/Mods.cs discovery, comparison and install of mod folders
src/SaveSync.Core/Lan/    network: framing, server, client, discovery, peer comparison
src/SaveSync.Core/GameLauncher.cs  starting the game, and a named save, from another PC
src/SaveSync.Watchdog/    tiny keep-alive process, reachable when the main one is not
src/SaveSync.Core.Tests/  218 tests: synthetic saves and mods, plus real TCP transfers on loopback
src/SaveSync.App/         WinForms UI (dark, 7DTD-styled), single screen, two buttons
tools/SaveSync.Probe/     dev CLI to drive the engine headlessly
docs/quick-start.html     printable instructions for the end users
```

`SaveSync.exe --diagnose` writes a report of everything discovery found to `%TEMP%`, so a
non-technical user can be asked to run one command and send the result back.

## Notes for future work

- Source is deliberately **pure ASCII**. A `.cs` file with no BOM is compiled using the machine's
  ANSI codepage, so a literal `·` renders as `Â·` on some machines and not others. `Theme.Dot`
  builds the character from its code point; `<CodePage>65001</CodePage>` is set as a second guard.
- WinForms docks in **reverse z-order**: the `Fill` panel must be added to `Controls` *first* or the
  docked header will paint over it.
- `StackPanel` overrides `ScrollToControl` because WinForms auto-scrolls a focused child into view,
  which was scrolling content off the top of the window on load.
- `Control.Visible` returns *effective* visibility, so it reads `false` on a child of a form that has
  not been shown yet — even immediately after setting it to `true`. Sizing logic must track intended
  visibility in its own field.
- Hiding a form inside `Load` does not work; WinForms shows it again straight after. Suppress the
  first show in `SetVisibleCore` instead, or the window flashes up at every login.
- The test suite runs with parallelisation disabled: the game-running guard is a process-wide switch
  that several tests flip deliberately.
- `Form.Load` only fires when a form is actually *shown*. A copy started with Windows is deliberately
  never shown, so hanging startup off `Load` left it running and completely inert - alive in Task
  Manager, doing nothing. Startup hangs off `OnHandleCreated` instead.
- A settings file can travel (copied between PCs, or sitting beside the EXE on the stick).
  `AdoptForThisMachine` regenerates the machine id when that happens: two PCs sharing one id each
  dismiss the other's announcements as their own echo and never connect, with nothing saying why.
- The firewall rule covers the whole port range the listener may fall back to, not just the first
  port, and is keyed to the port rather than the program path - the program lives on a stick, and a
  stick is not always the same drive letter.
