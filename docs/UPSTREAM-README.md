# Tenchu Recompilation

**Version 0.1.0** — early, incomplete release. See known issues below.

A launcher that turns **your own copy** of the PlayStation game *Tenchu: Stealth Assassins* into a
native build that runs directly on your machine, using a fork of
[RecompOne](https://github.com/BlackLabelHQ/RecompOne) to statically recompile the game's code into
C# rather than emulating it instruction by instruction.

> ### ⚠️ This is early and incomplete
>
> The game boots, plays and saves, but it has **not been played through**, and you should expect to
> run into problems. Known issues in this release:
>
> - **The intro cannot be skipped for the first ~20 seconds.** The controller is not live until the
>   game's interrupt chain starts. Nothing is wrong — it just will not respond yet.
> - **Auto-save is off by default.** The game ships it disabled, so no save prompt appears at the end
>   of a mission until you turn it on under Options. Saving itself works.
> - **Sound effects are unverified.** Cutscene dialogue and music play correctly; the rest of the
>   audio path has not been tested on this game and may be missing or wrong.
> - **Later missions are untested.** No full playthrough has been done, so endings and edge cases are
>   unknown territory.
> - **The launcher's artwork is placeholder.** The splash colours are stand-ins and the AppImage ships
>   without an icon.
>
> Save often, and keep more than one save.

## What this does and does not distribute

**No game code or data is included here, in any form.** The game's code and assets are copyrighted,
and anything derived from them carries that copyright. This repository contains only original work:
the launcher, its configuration, and a build definition.

The recompilation happens **on your machine, from a disc image you already own**. On first run the
launcher reads your disc, generates C# source from it into a local `game/` folder, compiles that,
and runs the result. Nothing generated is redistributed, and `game/` is never committed.

You will need:

- **Your own copy of Tenchu: Stealth Assassins (USA), serial SLUS-00706**, as a disc image with a
  `.cue`. The launcher checks the disc's boot executable and refuses anything else. See `DISC.md`
  for the reference dump's hashes and track layout.
- A **PlayStation BIOS is optional.** The engine is HLE and services BIOS calls itself. If you have
  your own dump you can point at it under Settings → BIOS.

The disc stays required at runtime, not just at setup.

## Building

You need the **.NET 10 SDK**. The engine lives in a **separate repository**, shared with the Alien
Resurrection recompilation, and is expected as a sibling directory:

```
some-directory/
├── RecompOne-fork/                 # the engine fork
└── Tenchu-SA-Recomp/               # this repository
```

```
dotnet build -c Release
```

If your engine checkout is somewhere else:

```
dotnet build -c Release -p:RecompOneDir=/path/to/RecompOne-fork
```

## Saves

Memory cards live in a per-user directory, independent of where you launch from:

| Platform | Location |
|---|---|
| Linux | `~/.local/share/Tenchu/` |
| Windows | `%LOCALAPPDATA%\Tenchu\` |

Change it under **Settings → Saves…**. A change applies on the **next launch**.

The Windows path follows from `Environment.SpecialFolder.LocalApplicationData` but has **not been
verified on Windows** — nothing in this project has been run there.

## The title font (optional)

The title screen looks for a display font named **`Tenchu.ttf`** in your save folder or next to the
executable. None is distributed here: display faces are commonly licensed "free for personal use",
which permits you to use one but grants this project no right to redistribute it. Without one the
launcher falls back to a font already installed on your system, at the same sizes.

## Credits

- **[RecompOne](https://github.com/BlackLabelHQ/RecompOne)** by BlackLabelHQ — the recompiler and
  runtime this is built on (MIT). This project uses a fork of it, and is not affiliated with or
  endorsed by them.
- **Tenchu: Stealth Assassins** — developed by Acquire, published by Activision in North America.
  All game code, assets and trademarks belong to their respective owners.
- **Libraries** — Dear ImGui, Silk.NET, NativeFileDialogSharp, MonoMod, and Roslyn.
- **Assets** — SDL_GameControllerDB; the Shinonome 16dot font by Yasuyuki Furukawa (public domain),
  maintained by /efont/, which the BIOS character fonts derive from.

Licenses differ between components; see each project's own license for the terms that apply to it.
