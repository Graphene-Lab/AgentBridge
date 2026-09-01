# Office Manager 16-Bit — Architecture

Technical overview for anyone maintaining or extending this game.
The game is a single-page 16-bit styled office simulator: the player moves a
boss around a top-down (oblique projection) office, hires employees and talks
to them through a text-adventure style chat.

## File layout

```
OfficeView/
├── index.html          # page: #gamebox (canvas + chat), font, scripts
├── style.css           # retro styling, palette taken from the office PNGs
├── game.js             # the whole game (~600 lines, vanilla JS, no deps)
├── assets/
│   ├── office.png          # non-walkable layer (walls, desks, furniture, floor transparent)
│   ├── office-ground.png   # walkable layer (complementary to office.png)
│   ├── office-ground.svg   # vector reference of the walkable area (source of truth)
│   ├── office-mask.js      # GENERATED: bit-packed walkable mask (see ASSETS.md)
│   ├── person.json         # LPC generator config of a generic employee (reference only)
│   ├── boss/, employee A..E/
│   │   ├── character.json  # LPC generator config (palette differences)
│   │   └── standard/       # walk.png (9 frames x 4 rows), idle.png (2 x 4), etc.
└── tools/
    └── make-mask.py        # regenerates office-mask.js from office-ground.png
docs-dev/
├── ARCHITECTURE.md     # this file
└── ASSETS.md           # asset inventory + how derived assets are produced
```

`index.html` loads `assets/office-mask.js` (plain `const` globals) before
`game.js`. Everything else is loaded asynchronously by `loadAssets()`.

## Coordinate system

The office is 1024×683 px, one fixed view (no scrolling). A character position
is its **feet point** `(x, y)` — a float. `y` grows downward (screen space).
The depth sort key of any object is the bottom of its sprite (`fy`).

### The walkable mask

`walkable(x, y)` answers "may a character stand here" from a precomputed
bit-packed mask (1 bit per pixel, row-major, MSB first). The mask is generated
from `office-ground.png` alpha > 40 by `tools/make-mask.py` because the browser
**cannot read PNG pixels on `file://`** (drawing a local `<img>` taints the
canvas and `getImageData` throws — see the "Canvas tainting" note below).

> **Gotcha — float coordinates:** positions are floats, so `walkable()` MUST
> truncate `x`/`y` to integers **before** the `y * width + x` lookup.
> Truncating after the multiply reads a pixel a whole row away (the classic
> symptom: characters sticking to invisible walls or drifting horizontally).
> Keep `x |= 0; y |= 0;` as the first lines of `walkable()`.

## Rendering — oblique projection with banded depth sorting

The scene has three visual layers, drawn in this order every frame:

1. `office-ground.png` (the floor),
2. characters and `office.png` **interleaved by depth**,
3. overlays (wall clock, "boss" label, speech bubbles).

The two office PNGs are perfectly complementary (transparent pixels of one are
opaque in the other), so `office.png` can be treated as "the room minus the
floor".

**The band algorithm** (`render()`): because furniture in the room is taller
than the characters, a character standing behind a desk must be drawn *under*
the desk pixels that appear above it on screen, while a character in front
must be drawn *over* them. Instead of slicing the furniture into per-object
sprites, the renderer cuts `office.png` into **horizontal bands at the
characters' feet Y**:

```
floor
for each character sorted by feet Y ascending:
    draw office.png rows [cursor, char.fy)   # everything behind this char
    draw the character
    cursor = char.fy
draw office.png rows [cursor, H)             # everything in front of all chars
```

This reproduces the projection rules exactly:
- the body may overlap the back wall until the feet reach the wall base,
- a character in front of a desk is drawn over it,
- a character right behind a desk has only its legs hidden (the desk's pixels
  at the character's columns are drawn over the lower part of its body).

Because `office-ground.png`/`office.png` are complementary, collision is done
against the mask while rendering uses the two images — they can never disagree.

## Characters

`class Person` holds `x, y` (feet), `dir`, animation state, bubble and
`canStand()` — collision against `standable()`.

### Walking behind objects (the "behind" band)

The mask only covers the floor, so a character could never stand with its feet
inside a desk's screen footprint — it stopped a fixed distance south of the
furniture. `buildBehind()` precomputes a **behind band**: for every opaque
vertical run that has walkable floor directly above it (i.e. it can be entered
from the north), the top `BEHIND_PX = 32` rows become standable too. This lets
characters tuck their feet behind desks: the banded renderer then hides the
lower part of the body behind the object ("legs hidden, body out") while the
depth limit (`BEHIND_PX`) prevents walking through furniture. Runs against the
image top (the back wall itself) get no band.

`standable(x, y)` = walkable **or** inside the behind band; `canStand()`
samples the feet and ±16 px for the shoe span. Positions are floats — see the
"Canvas tainting" note for why the mask lookup must truncate first.

**Occlusion of tucked characters:** the banded renderer would slice `office.png`
at the character's feet, so desk pixels *above* the feet ended up drawn *under*
the character — a tucked character stood visibly on top of the desk. The sort
key is therefore `renderDepth(p)`: for a character whose feet are inside a
behind band, the depth becomes the object's top row (`BEHIND_TOP`), so the
whole object is drawn over the character's lower body (head/torso stay visible
above the desk, legs hidden). Verified: a boss tucked at (250, 270) behind desk
row 1 renders 0 red (vest) pixels below the desk's top edge.

### Sprites and the direction limitation

Each sheet is 832×256 px: 13 columns × 4 rows of 64 px frames. Walk sheets
contain a 9-frame cycle (cols 0–8); idle sheets only 2 frames (cols 0–1).

**Pose layout (verified by pixel census):** the generator exported
`row 0 = back view` (mostly hair), `row 1 = left profile`, `row 2 = front`,
`row 3 = right profile`. There is no 3/4 diagonal. `DIRS` therefore maps:

```
N  → row 0 (back)   NE → row 3 (right)   E → row 3   SE → row 3
S  → row 2 (front)  SW → row 1 (left)    W → row 1   NW → row 1
```

Pure up shows the back; all lateral movement (including the diagonals) uses
the profiles so the sprite always hints at the direction of travel. Earlier
versions mapped `S → row 0` / `N → row 1` (down walked with the back) and
`NW/NE → row 0` (diagonal ups showed the back) — keep the mapping above.

### Animation

`anim(dt)` advances `frame` through 9 walk frames (100 ms) or 2 idle frames
(600 ms). **On a move/stop transition the frame counter is reset** — otherwise
the last walk frame (e.g. 5) is drawn from the idle sheet, where column 5 is
empty, making the character flash invisible for up to ~0.6 s.

Sprites are rendered at `CHAR_SCALE = 2` (128 px frames); the sprite anchor is
the feet point, all geometry offsets (bubble, label, hit test) use `CHAR_H`.

## Employee AI — wandering, work spots, coffee machine

Employees start **in front of the door** (the back strip, x ≈ 72..156) and
wander between random waypoints using the **navigation grid + BFS**:

- `NAV_CELL = 32` px → 32×22 grid; a cell is passable if its centre and its
  four edge midpoints (±14 px) are walkable — the edge samples are what let
  the grid see the thin desk-chair protrusions, which a centre-only check
  would miss (employees would otherwise path straight through a chair and jam).
- `pathTo(sx, sy, tx, ty)` runs BFS over 4-neighbour passable cells.
- `updateEmployee()` follows the waypoint list; on arrival it idles and picks
  a new random reachable target. Stuck detection (no progress for 2 s) replans.

**Desk work spots:** each desk has a central front position (`WORK_SPOTS` —
desk centre x, front base y + 2):

```
desk 1 (229,337)  desk 2 (497,336)  desk 3 (771,333)
desk 4 (228,491)  desk 5 (497,489)  desk 6 (771,486)
desk 7 (334,640)  desk 8 (652,636)
```

When an employee passes in front of a desk (within `WORK_R = 55` px of a spot)
it is **attracted** to it:

- The **20 s work deadline and the cooldown start at the attraction moment**
  (`workDeadline`), not at arrival — an employee jammed on the chair without
  reaching the spot is still unlocked after 20 s and is **no longer attracted**
  during `WORK_COOLDOWN_MS` (15 s).
- The route to the spot goes via an **approach point in the aisle** below the
  desk (`y + 24`) and then straight up through the walkable gap — the chair is
  drawn right at the spot, so the approach must come from below, never across.
- If the spot is already taken by another employee (heading or working), the
  new arrival stands **30 px to the right** of it.
- The working employee stands still and **keeps its current direction** (no
  forced facing); it occupies the spot (`workAt`) until the deadline, then
  resumes wandering.

**Coffee machine:** `COFFEE_ZONE` is the floor in front of the black coffee
machine (brown cabinet, x ≈ 765..800). Anyone (boss or employees) passing
through says **"I'm taking a coffee break"** (persistent bubble for the boss
while inside, one line per employee with a 30 s cooldown).

## Engagement state machine

Only one employee can be hired at a time. Hire methods:

| Method | Trigger | Engagement ends when |
|---|---|---|
| contact | boss within `ENGAGE_R` (70 px, feet distance) | boss moves beyond `DISENGAGE_R` (92 px) or 1 min of boss inactivity |
| tab | nearest employee on the first press, then **cycles** through the roster (each press releases the current hire and hires the next, wrapping) | 1 min of boss inactivity |
| mouse | click on the employee sprite | 1 min of boss inactivity |
| — | **Escape** releases the current hire at any time | — |

`engageNextTab()` implements the cycling: the released employee gets a
`REENGAGE_PAUSE` cooldown so the contact check cannot instantly re-hire it
(which would make Tab flip-flop between two nearby employees).

The inactivity clock is **`engagedSince`**, started at the hire moment and
refreshed by **boss speech only** (`markBossSpeech()` — typed messages, the
phone and the coffee lines). **Boss movement deliberately does NOT refresh
it**: a hire (e.g. from Tab) must end after 1 minute even while the boss keeps
walking, so proximity hiring automatically reactivates once nobody is hired
(a moving boss would otherwise keep the tab-hired employee engaged forever and
block proximity hiring indefinitely). Contact hires additionally end when the
boss leaves the area (`DISENGAGE_R`).
A hired employee stops, faces the boss, and gets a persistent yellow **"!"**
marker above its head (drawn above the current speech bubble), plus the
"Boss, give orders!" greeting. Details that prevent visual glitches:

- Hiring an employee clears its current path and pauses it — a hired employee
  stands still and never resumes a route that leads back through the boss zone.
- After a contact fire the employee is paused for `REENGAGE_PAUSE` (1.5 s) and
  the contact check skips paused employees — no stop/start oscillation when the
  boss lingers near the boundary.
- Boss "activity" = moving or speaking (`lastBossActivity`), used both for the
  60 s fire timer and for the "boss" label.

## Wall phone

`PHONE_ZONE` is a rectangle on the floor in front of the wall phone. While the
boss's feet are inside, the bubble persistently shows **"I have to call"**
(speech: logged once on entry, bubble stays until the boss leaves). On the
phone, typed chat messages are logged without replacing the bubble.

## Speech: bubbles + chat log

`Person.say(text, persist)` sets the sprite bubble (adaptive, multi-line,
white with black border, tail towards the speaker) and pushes the line to the
chat console. All speech is logged; **engagement events are not**.

`Chat` is a typewriter queue (30 ms/char) that appends to `#log` and
auto-scrolls. User text is HTML-escaped (`esc()`).

The **"boss" label** appears above the boss after 10 s of idleness and hides on
any movement or speech; it also hides while the phone line is active.

## Wall clock

`drawClock()` draws a live analogue clock (hour/minute/second hands) on the
right side of the back wall at `CLOCK = { x: 965, y: 72, r: 26 }` — a spot
verified to be plain wall in the PNG.

## Audio

`Sfx` synthesizes all effects with Web Audio (no files): square/triangle
oscillators with exponential decay — steps, hire arpeggio, send/reply blips,
ambient line, phone ring. `Sfx.ensure()` is defensive (never throws) and is
called on the first key/click so the audio context is created within a user
gesture. A SOUND:ON/OFF button toggles `Sfx.muted`.

## Input

- Arrow keys move the boss (held-key state, diagonal normalisation).
- `Tab` hires the nearest employee (then cycles); `Escape` releases the
  current hire; `Enter` sends the chat message.
- The focus stays on the chat `<input>` at all times: autofocus + refocus on
  any click and on window blur; the key handlers call `preventDefault()` for
  arrows/Tab/Enter so the caret and focus never move.
- Mouse click on the canvas maps screen→office coordinates (canvas scale) and
  hires the clicked employee.

## Layout & scaling

`#gamebox` wraps the canvas and the chat in one centred column so **the chat
is never wider than the office** — together they form a single rectangle.
`fitCanvas()` computes `scale = min(innerWidth/1024, (innerHeight−208)/683)`
and sets the box/canvas width; the canvas is upscaled with
`image-rendering: pixelated` for the chunky 16-bit look.

## Config constants

All tunables are at the top of `game.js`: speeds, radii, timers, the phone
zone, the clock spot, the navigation cell, the sprite scale, the ambient-line
schedule (12 s floor + exponential tail, mean ≈ 60 s per employee).

## Canvas tainting on file:// (why the mask is precomputed)

`index.html` runs directly from disk. In Chromium, drawing a `file://` image
onto a canvas taints it, so `getImageData()` throws — that is why the walkable
mask is shipped as generated data (`assets/office-mask.js`) instead of being
read from the PNG at startup. Do not replace the mask with a runtime pixel
read unless you require serving the game over HTTP (where the taint does not
apply) — and even then, the embedded mask is exact and faster.
