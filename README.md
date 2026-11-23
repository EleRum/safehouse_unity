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

**Quests**
- A data-driven chain in `data/quests.json`: six quests across Prapor and Therapist, with objectives of
  kind `extract`, `loot` and `deliver`, and rewards of roubles, standing and items. `QuestLoader`
  checks every cross-reference and refuses cycles; `QuestRules` derives each status (Locked / Open /
  Done / Claimed) instead of storing it.
- The QUESTS screen lists the chain, shows what each quest wants, and its CLAIM button only pays out
  once the objective is met. Finishing an expedition advances the chain, so trader standing can be
  earned rather than typed into the GM box.
- 16 EditMode tests cover the rules, alongside the 11 for the snapshot repository.

**Character creation**
- GEAR opens the data folder and hands over to CHARACTER on the next frame, so the game starts with
  *who am I playing*. The roster shows every character with a portrait, name, status and money; OPEN
  and DELETE always respond — both used to be disabled for the character already open, which with a
  single save meant the whole roster was dead.
- NEW CHARACTER sits above the saves and opens a separate two-page flow: pick a portrait, then fill in
  the sheet. A portrait comes from `icon_cache/avatars` and is remembered in `PlayerPrefs`, so the save
  format is untouched.
- Identity fields are text boxes with a preset menu this screen draws itself. A `DropdownField`'s popup
  is attached outside the document's tree, where this document's stylesheet cannot reach it, so it came
  out in Unity's white default skin.
- Abilities are any whole number from 1 to 10, paid for out of the same ten-point pool. They used to be
  four tiers (3, 5, 7, 10) and `AbilityCost` threw on anything else.
- A **starting kit** — four presets (RIFLEMAN, SCOUT, BREACHER, MARKSMAN) — puts a weapon, ammunition
  and a bandage into the new character's stash.

**Opening stashed cases**
- `data/case_openings.json` ties eight cases (weapon, ammunition, medicine, grenade, key, toolbox, junk
  box) to the `containers.json` tables the raid nodes already search. Two of them want a key.
- OPEN consumes the case and rolls its contents into the stash; a locked case names the key it needs
  instead of failing quietly. Keys are not consumed, the way a door key is not.
- Fence now prices all 4832 items, so loot is never dead weight: 40% of base value, capped under the
  cheapest offer so the load-time arbitrage check still passes. Until now 556 items could not be sold
  to anyone.

**Healing**
- Nothing outside a fight ever wrote to body parts, so a character who came home wounded stayed wounded
  for good; the hideout's Medstation does nothing to this day.
- USE applies a medkit from the stash: its pool closes the worst wounds first, its flags clear the
  conditions it treats, one item is consumed — and a kit that would change nothing is refused rather
  than burned.
- The GEAR bottom bar lists bleeding and fractures as well as missing hit points. It used to read
  "no injuries" for a character at full health carrying two fractures and a heavy bleed.

**Carrying**
- Equipping a rig or backpack resizes its grid to that item's own capacity. The lattice is an element
  declared at a fixed size in the UXML, so the number above it changed while the squares did not; both
  follow the grid now.
- An empty rig or backpack is 2x3. It used to be 6x4 / 6x8 — as large as the biggest rig and backpack in
  the data — so putting a bag on could only make the carried space smaller.
- `GearData.GridDimensions` holds the capacity exactly instead of rounding up to six columns, which was
  handing an eight-cell bag twelve cells.
- `migrate_grids.py` (next to `reset_trip.ps1`) brings existing saves onto these rules: it re-places
  what a grid holds, moves what no longer fits into the stash, refuses to write a save it cannot fit,
  and leaves a `.before-grids` copy behind.

**Tests** — 11 EditMode tests for the snapshot repository: byte-for-byte round trip, ordering,
pruning to six, two snapshots in the same second, a foreign character's snapshot, a damaged file
being refused without touching the real save, and a reason that tries to escape its folder.

---

## Known gaps

  screen, so the loyalty chain is intentionally incomplete.
- **Items cannot hold items.** A case is a flat object with a footprint, not a grid of its own, which
  is why opening one consumes it instead of storing things inside it. Nesting would need a save format
  change.
- **Painkillers do nothing.** There is no `pain` condition - only `light_bleed`, `heavy_bleed` and
  `fracture` - so the 40 of 56 medical items outside `gear.json`'s sixteen-entry `meds` table are loot
  to sell, not medicine to use.
- **The Medstation does nothing.** No facility except the stash, the generator and production is read.
- **No main menu and no manual save.** The game opens the last character; every change is written
  immediately.
- **No rotation on the TRADERS screen** — items can be moved there but not turned; GEAR has R.
  numbers. The SCAVENGE party panel's seven body-part bars *are* real.
- **The Gundog sheet is stored, not read.** Its eight abilities use a different vocabulary from
  the profile's own eight, and only the latter feeds an expedition.
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
