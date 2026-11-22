# Safehouse

A stash-management and expedition game for PC, built in Unity. You keep a hideout, kit out a
character, send them into a raid zone, walk the node map, come back with loot — and spend what you
come back with on traders and facilities. Combat is automatic; the decisions are what you carry,
where you go, and when to extract.

The setting and item catalogue are from *Escape from Tarkov* (via [tarkov.dev](https://tarkov.dev));
the rules, maps, expedition structure and interface are this project's own.

This repository is a fork of [smandylee/safehouse_unity](https://github.com/smandylee/safehouse_unity)
and the changes made in it are listed under [What this fork adds](#what-this-fork-adds).

---

## Running it

| | |
|---|---|
| Unity | **6000.6.2f1** (opening in an older editor will ask to upgrade the project) |
| Scene | `Assets/_Project/Scenes/Gear.unity` |
| Entry point | that scene — press Play; there is no main menu |

```
"C:\Program Files\Unity\Hub\Editor\6000.6.2f1\Editor\Unity.exe" ^
  -projectPath "D:\path\to\safehouse_unity"
```

First launch takes a few minutes: Unity rebuilds `Library/` from scratch.

If the interface looks empty or unstyled, rebuild the scene from the menu:

```
Safehouse > Build Gear Scene
```

That generator writes `Scenes/Gear.unity` and `UI/GearPanelSettings.asset` — both are checked in,
so only run it after pulling changes to a `.uxml`.

### Building an executable

`File > Build Settings`, drag `Assets/_Project/Scenes/Gear.unity` into **Scenes In Build**, target
Windows/Standalone, Build. The scene list is empty in the repository, so this has to be done once.

---

## Playing it

The top bar moves between screens. All six are one scene; only one is visible at a time.

| Screen | What you do there |
|---|---|
| **CHARACTER** | The roster — open, create or delete a character — and the Gundog Revised sheet for the one below it |
| **GEAR** | The stash grid, the rig and backpack grids, and seven loadout slots. Drag to move, **R** to turn, **Esc** to cancel a drag |
| **HIDEOUT** | Shared facilities: upgrades, fuel, and production recipes |
| **TRADERS** | Nine traders: loyalty, stock, buy and sell |
| **SCAVENGE** | Send the open character out: pick a map, day or night, and either the node map or the clock |
| **SETTINGS** | Where the save folder is |

**The loop.** Kit out on GEAR → launch from SCAVENGE → walk the node map, dealing with each node
(loot, a body, a noise, a boss lair) → reach an exit and extract with what you found → sell on
TRADERS and upgrade the HIDEOUT → go out again.

Every change is saved as it happens. There is no save button and no save slots; a character is one
file, and the game opens the last one you had open.

**Two expedition modes.**
*Node map* is the one to play: the trip only moves when you choose a node, each node rolls its own
loot and fight, and the whole trip is capped at 40 nodes.
*Clock* runs on real time — outer 30 minutes, centre 45, deep 60, night ×1.5 — and is for leaving
running; nothing happens on that screen until the timer runs out.

---

## Item icons and portraits

The repository contains **no game art**. `icon_cache/` is git-ignored, because the images are
Escape from Tarkov art and must not be redistributed. It sits next to `Assets/`, not inside it, so
Unity never imports it; the game reads it from disk the first time an item is drawn.

```
icon_cache/48/<item_id>.png       inventory icon, exactly the item's footprint at 48 px per cell
icon_cache/art/<item_id>.png      text-free render for the LOADOUT cards, up to 256 px
icon_cache/traders/<id>.png       one 128 px portrait per trader
```

An item with no icon falls back to a three-letter monogram, so the game is fully playable without
any of this — it just looks like a wireframe. `tools/import_art.py` is the original downloader and
needs the Python build's `tarkov_snapshot.json.gz`; the catalogue can also be filled by joining
`Assets/StreamingAssets/data/items.json` against `json.tarkov.dev` on `normalizedName`, which is the
same id.

---

## Where things live

```
Assets/_Project/Scripts/
    Core/     Plain C# rules. The assembly is compiled with noEngineReferences, so it cannot call
              UnityEngine at all and its rules are testable without a scene.
    Data/     JSON loading, saving, migrations, snapshots. Talks to Core; no rules of its own.
    UI/       MonoBehaviours. Renders and forwards input, never decides anything.
    Editor/   Scene generation and the Python save importer.
Assets/StreamingAssets/data/     The rule data as editable JSON: items, traders, gear, maps,
                                containers, combat, raid_nodes, locations, hideout, recipes.
Assets/Tests/EditMode|PlayMode/  Unity Test Framework tests, 24 files.
Assets/_Project/README.md        The deep dive: per-module notes, why the folders are split this
                                way, and what is and is not ported.
```

**Save folder** — `SAFEHOUSE_UNITY_DATA_DIR` if set, otherwise
`%USERPROFILE%\AppData\LocalLow\DefaultCompany\Safehouse\Safehouse`:

```
account.json                    shared hideout and character order
saves/<id>.json                 one file per character (+ .bak of the previous version)
snapshots/<id>/<unix>-<reason>.json   the "before this trip" copies ROLL BACK uses
expeditions/<id>.json           the trip a character is on, if any
recovery/                       a damaged save that was replaced from its backup
```

Saves are written atomically and the previous version is kept as `.bak`; a file that cannot be read
is moved to `recovery/` and restored from its backup. Schema migrations run from v1 through v9, so
saves from the earlier Python build open here.

`reset_trip.ps1` (outside this repository) drops any unfinished trip and clears the `on_expedition`
flag — the way out if a character is stranded in a trip that cannot be finished.

---

## What this fork adds

Everything below is new relative to upstream.

**Save system**
- `SnapshotRepository` — a ring of six profile documents per character, written with the same
  serializer and atomic write as the real save.
- Every expedition launch keeps one first, and the SCAVENGE screen offers **ROLL BACK**, which
  validates the snapshot, drops the trip file and commits the character back. Until now a character
  that launched a trip could not get out of it except by walking to an exit.
- `ExpeditionRepository.Delete`, so a rolled-back trip cannot be picked up again.

**Interface**
- **CHARACTER** now has a roster: every character in the data folder, with OPEN and DELETE. The
  repository could always list and delete; nothing exposed it.
- The **GEAR** toolbar and bottom bar stop being decorative: DEPLOY opens SCAVENGE, TO HIDEOUT opens
  the hideout, EQUIP wears the selected item in the slot its type belongs to, SORT repacks the stash
  by category, ALL/MEDS/BARTER filter it, and SAVE PRESET exports the loadout and stash as JSON.
  The QUESTS tab stays disabled — there is no quest system behind it.
- **TRADERS** accepts drags: onto the trade panel to offer an item, onto SELL to arm the sale (the
  confirmation still has to be pressed), or inside the grid to move it — so a fragmented stash can
  be tidied without leaving the screen. The drag copy is the item's real footprint.
- Text fields are dark everywhere. Unity's default skin paints `.unity-text-input` white and the
  CHARACTER form set that colour inline as well, so both layers had to change.
- The SCAVENGE screen centres its design box like the other screens, defaults to the node map
  rather than the clock, and draws its buttons in the project's own style.
- The Gundog character sheet is in English; it was hard-coded Korean, which no bundled font could
  render.

**Tests** — 11 EditMode tests for the snapshot repository: byte-for-byte round trip, ordering,
pruning to six, two snapshots in the same second, a foreign character's snapshot, a damaged file
being refused without touching the real save, and a reason that tries to escape its folder.

---

## Known gaps

- **No quests.** The trader standing that loyalty levels need is still raised by hand on the TRADERS
  screen, so the loyalty chain is intentionally incomplete.
- **No main menu and no manual save.** The game opens the last character; every change is written
  immediately.
- **No rotation on the TRADERS screen** — items can be moved there but not turned; GEAR has R.
- The GEAR bottom bar's weight and health figures are hand-written text, not the character's real
  numbers. The SCAVENGE party panel's seven body-part bars *are* real.
- The Gundog sheet (abilities, skills, classes, arts) is stored but not read by expeditions; the
  screen says so.
- Trader portraits and item icons are not in the repository (see above), so a fresh clone has none.
- Korean text in character bios has no bundled font.

---

## Credits

- Item, trader, map and container data: [tarkov.dev](https://tarkov.dev) and
  [json.tarkov.dev](https://json.tarkov.dev).
- Game art: *Escape from Tarkov*, © Battlestate Games. Not included, not redistributed, private use
  only.
- Fonts: Oswald and JetBrains Mono, both SIL Open Font License, bundled with their `OFL.txt`.
- Upstream project: [smandylee/safehouse_unity](https://github.com/smandylee/safehouse_unity).

COMP4122 Game Development, group project.
