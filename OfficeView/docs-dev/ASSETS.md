# Office Manager 16-Bit — Assets

Inventory of the art assets and how the derived/generated files are produced.

## Scene images

| File | Role |
|---|---|
| `office.png` | Non-walkable layer: back wall (with door, windows, wall phone), 8 desks in 3 rows (3+3+2), photocopier (left), shelf (right), wall furniture. The floor is transparent. |
| `office-ground.png` | Walkable layer: the floor. Exactly complementary to `office.png` (opaque pixels of one are transparent in the other). |
| `office-ground.svg` | Vector trace of the walkable area — the deterministic reference of where characters may walk. |

### Geometry reference (measured from the pixels)

```
wall band        y 0..146   (base is jagged 147..178 — furniture against the wall)
back strip       y 147..217 (floor behind desk row 1)
desk row 1       3 desks    y 218..337   (bases ≈ 337)
aisle            y 338..386
desk row 2       3 desks    y 370..490   (bases ≈ 490)
aisle            y 475..532
desk row 3       2 desks    y 518..639   (bases ≈ 639)
front strip      y 624..682
photocopier      x 29..105, y 328..449   (base 449)
shelf            x 910..999, y 309..449  (base 449)
```

**Back wall, left → right:** door (x ≈ 76..151, dark wood, no protrusion), black
wall phone (x ≈ 152..190, black body + blue screen + handset at y ≈ 110..122 —
floor zone `PHONE_ZONE`), plants (x ≈ 220..256), glass window (x ≈ 292..681).
Both the phone and the window are wall-mounted (no floor protrusion).

**Back wall floor protrusions, right → left** (objects standing on the floor
against the wall, in this order):

| # (from right) | Object | Position |
|---|---|---|
| 1 | trash bin | x ≈ 916..940 |
| 2 | brown cabinet with **black coffee machine** on its left part | cabinet x ≈ 765..885; machine x ≈ 765..800 — floor zone `COFFEE_ZONE` |
| 3 | water dispenser (blue jug) | x ≈ 684..736 |
| 4 | cabinet with fax / teletypewriter | x < 684 |
| 5 | shelf with binders | x < 684 |
| 6 | plant | leftmost, x < 684 |

**Desk chairs:** every desk has a chair drawn in front of it (a small floor
protrusion, slightly left of the desk centre, around y ≈ desk base + 1..50).
The chairs are part of `office.png` (non-walkable) and their thin shapes can be
missed by coarse navigation grids — see ARCHITECTURE.md "Employee AI".

```
clock spot       x 916..1013, y 35..111 (plain wall) — CLOCK is drawn at (965, 50)
```

The desk sprites are trapezoid-ish in perspective; their per-column opaque
extent is what the banded depth sorting relies on (see ARCHITECTURE.md).

## Generated file: `assets/office-mask.js`

Bit-packed walkable mask (1 bit per pixel, row-major, MSB first) embedded as
base64. It is generated from `office-ground.png` (alpha > 40):

```
python tools/make-mask.py
```

Regenerate it whenever `office-ground.png` changes. The browser cannot read
PNG pixels on `file://` (canvas tainting), so this data file is the runtime
source of truth for collision — see ARCHITECTURE.md "Canvas tainting".

## Characters

Six characters, one folder each: `boss/`, `employee A/` … `employee E/`.
Each folder contains:

- `character.json` / `assets/person.json` — Universal-LPC generator configs
  (the LPC layers that compose the sprite). `person.json` describes a generic
  employee; the per-folder `character.json` files record the palette choice.
- `standard/walk.png` — 832×256 px: 9-frame walk cycle (cols 0–8) × 4 rows.
- `standard/idle.png` — 832×256 px: 2-frame idle cycle (cols 0–1) × 4 rows.
- other `standard/*.png` sheets (run, shoot, spellcast, …) are unused.

### Sheet layout

Rows (verified by pixel census): `0 = back` (mostly hair), `1 = left profile`,
`2 = front`, `3 = right profile`. There is no 3/4 diagonal pose. `DIRS` in
`game.js` maps N/NE/NW onto the back row, E/SE onto the right profile,
S onto the front row and W/SW onto the left profile; see ARCHITECTURE.md
"Sprites and the direction limitation".

The character's visible pixels occupy roughly x 17..46, y 14..60 within the
64×64 frame (feet at the bottom, around y ≈ 60). Frames are drawn at
`CHAR_SCALE = 2` with the feet anchored on the position.

### Palette differences

The employees share the same sprite geometry; only the jacket colour differs:

| Character | Jacket (from character.json) |
|---|---|
| boss | red vest |
| employee A | slate vest / beige cardigan |
| employee B | white vest, pale cardigan |
| employee C | **blue cardigan** (recolored — see below) |
| employee D | gray vest |
| employee E | **green cardigan** (recolored — see below) |

B, C and E were exported with identical white vest + pale cardigan, so C and E
were recolored offline to blue and green to keep them visually distinct.
The recolor was applied to the two pale cardigan shades
`(224,224,192) → (120,190,235) | (140,215,150)` and
`(192,176,152) → (70,130,195) | (85,165,105)` on `walk.png` and `idle.png`
with a small Pillow script (run once; the script is not committed). If the
sheets are regenerated, either re-run an equivalent recolor or export C/E with
different jacket colours directly from the generator.

## UI palette (from the PNGs)

The chat/label colours are taken from the scene so the UI stays coherent:

| Use | RGB | Note |
|---|---|---|
| chat background | (23,13,2) | near-black brown |
| chat border / text | (186,162,114) | wall tan |
| boss name | (198,72,60) | muted boss vest red (168,24,24) |
| employee name | (110,160,200) | muted employee C jacket blue (120,184,232) |
| dim / system text | (110,92,66) | dimmed tan |
| input text | (232,218,176) | light tan |

## Third-party sources

- Office scene: user-provided (`office.png`, `office-ground.png`, `.svg`).
- Character sprites: [Universal-LPC Spritesheet Character Generator]
  (https://liberatedpixelcup.github.io/Universal-LPC-Spritesheet-Character-Generator/);
  the per-folder `character.json`/`credits/` files carry the layer credits and
  licenses (OGA-BY 3.0, CC-BY-SA 3.0, GPL 3.0).
- Font: "Press Start 2P" via Google Fonts (with monospace fallback — the game
  works offline, the fallback just looks less retro).
- Sound effects: synthesized at runtime with Web Audio (no files).
