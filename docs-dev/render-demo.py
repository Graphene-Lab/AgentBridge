#!/usr/bin/env python3
"""Render a scripted demo of the AgentBridge TUI to demo.gif and demo.mp4 (H.264).

The animation is a faithful reconstruction of the real Terminal.Gui layout:
menu bar, AGENT logo panel (multicolor ASCII art), Chat panel with streaming
replies, / command palette with live filtering, input line and status bar.

Outputs (next to this script):
  demo.mp4  — full resolution, 25 fps, H.264 (yuv420p)
  demo.gif  — scaled down, ~12.5 fps, looping
"""

import math
import os

from PIL import Image, ImageDraw, ImageFont
from fontTools.ttLib import TTFont

ROOT = os.path.dirname(os.path.abspath(__file__))

# ---------------------------------------------------------------------------
# Constants (mirror AgentBridge/Tui.cs geometry and colors)
# ---------------------------------------------------------------------------
COLS, ROWS = 110, 31
LOGO_W = 48                      # left panel width incl. frame border
FONT_SIZE = 24

BLACK = (10, 12, 16)
WHITE = (222, 227, 235)
GRAY = (128, 135, 148)
CYAN = (0, 190, 220)
MAGENTA = (222, 118, 222)
BLUE = (76, 150, 236)
RED = (242, 96, 105)
GREEN = (96, 205, 128)
FRAME = (108, 118, 134)
MENUBG = (36, 40, 49)
MENUTXT = (214, 219, 227)
CHROME = (26, 29, 36)
CHROME_TXT = (160, 170, 186)
CHROME_EDGE = (58, 64, 78)

LOGO_GRADIENT = [BLUE, BLUE, MAGENTA, MAGENTA, RED, RED]

LOGO = [
    " █████╗  ██████╗ ███████╗███╗   ██╗████████╗",
    "██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝",
    "███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║",
    "██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║",
    "██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║",
    "╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝",
]

CHAT_INNER_W = COLS - LOGO_W - 2

# ---------------------------------------------------------------------------
# Fonts / glyph resolution (per-char fallback chain)
# ---------------------------------------------------------------------------
FONT_CANDIDATES = [
    r"C:\Windows\Fonts\CascadiaMono.ttf",
    r"C:\Windows\Fonts\consola.ttf",
    r"C:\Windows\Fonts\seguisym.ttf",
    r"C:\Windows\Fonts\arial.ttf",
]


def _cmap(path):
    try:
        f = TTFont(path, lazy=True)
        cmap = f.getBestCmap() or {}
        f.close()
        return cmap
    except Exception:
        return {}


_CHAIN = [(p, _cmap(p)) for p in FONT_CANDIDATES if os.path.exists(p)]
assert _CHAIN, "no usable fonts found"


def font_path_for(ch):
    for p, cmap in _CHAIN:
        if ord(ch) in cmap:
            return p
    return _CHAIN[0][0]


_font_cache = {}
_GLYPH_CACHE = {}


def _font(path, size):
    if path not in _font_cache:
        _font_cache[path] = ImageFont.truetype(path, size)
    return _font_cache[path]


def cell_metrics():
    f = _font(FONT_CANDIDATES[0], FONT_SIZE)
    asc, desc = f.getmetrics()
    return int(math.ceil(f.getlength("M"))), asc + desc


def glyph(ch, color, bold=False):
    path = font_path_for(ch)
    key = (path, color, bold)
    cache = _GLYPH_CACHE.get(key)
    if cache is None:
        cache = {}
        _GLYPH_CACHE[key] = cache
    img = cache.get(ch)
    if img is None:
        cw, chh = cell_metrics()
        img = Image.new("RGBA", (cw, chh), (0, 0, 0, 0))
        d = ImageDraw.Draw(img)
        d.text((0, 0), ch, font=_font(path, FONT_SIZE), fill=color + (255,),
               stroke_width=1 if bold else 0, stroke_fill=color + (255,))
        cache[ch] = img
    return img


# ---------------------------------------------------------------------------
# Grid model
# ---------------------------------------------------------------------------
class Cell:
    __slots__ = ("ch", "fg", "bg", "bold", "ul")

    def __init__(self, ch=" ", fg=WHITE, bg=BLACK, bold=False, ul=False):
        self.ch, self.fg, self.bg, self.bold, self.ul = ch, fg, bg, bold, ul


class Grid:
    def __init__(self):
        self.cells = [[Cell() for _ in range(COLS)] for _ in range(ROWS)]

    def put(self, r, c, ch, fg=WHITE, bg=BLACK, bold=False, ul=False):
        if 0 <= r < ROWS and 0 <= c < COLS:
            self.cells[r][c] = Cell(ch, fg, bg, bold, ul)

    def text(self, r, c, s, fg=WHITE, bg=BLACK, bold=False, ul=False):
        for i, ch in enumerate(s):
            self.put(r, c + i, ch, fg, bg, bold, ul)

    def hline(self, r, c0, c1, ch, fg=FRAME, bg=BLACK):
        for c in range(c0, c1 + 1):
            self.put(r, c, ch, fg, bg)

    def vline(self, c, r0, r1, ch, fg=FRAME, bg=BLACK):
        for r in range(r0, r1 + 1):
            self.put(r, c, ch, fg, bg)

    def fill(self, r0, c0, r1, c1, ch=" ", fg=WHITE, bg=BLACK):
        for r in range(r0, r1 + 1):
            for c in range(c0, c1 + 1):
                self.put(r, c, ch, fg, bg)

    def frame(self, r0, c0, r1, c1, title=None, title_fg=WHITE):
        self.hline(r0, c0, c1, "─")
        self.hline(r1, c0, c1, "─")
        self.vline(c0, r0, r1, "│")
        self.vline(c1, r0, r1, "│")
        self.put(r0, c0, "┌")
        self.put(r0, c1, "┐")
        self.put(r1, c0, "└")
        self.put(r1, c1, "┘")
        if title:
            self.text(r0, c0 + 1, f" {title} ", fg=title_fg)


# ---------------------------------------------------------------------------
# Text helpers
# ---------------------------------------------------------------------------
def wrap(text, width):
    lines, cur = [], ""
    for w in text.split(" "):
        if not cur:
            cur = w
        elif len(cur) + 1 + len(w) <= width:
            cur += " " + w
        else:
            lines.append(cur)
            cur = w
    if cur:
        lines.append(cur)
    return lines or [""]


def truncate(s, width):
    return s if len(s) <= width else s[: width - 1] + "…"


# ---------------------------------------------------------------------------
# Demo script (simulated state machine)
# ---------------------------------------------------------------------------
PROMPT = "Draft a quote for the Rossi account: March maintenance renewal, 12 seats, PDF to sign."

REPLY = (
    "Here's the draft quote for the Rossi account:\n\n"
    "Quote #Q-2026-0412 — Rossi S.r.l.\n"
    "· Maintenance renewal (12 seats)  €4,800.00\n"
    "· Priority support (annual)       €1,200.00\n"
    "Total (excl. VAT)                 €6,000.00\n\n"
    "I generated the PDF (Quote_Rossi_2026.pdf) and attached the DOCX "
    "for review. I can email it to the client or adjust the terms — "
    "just say the word."
)

SERVER = "http://localhost:5290"
STREAM_START, STREAM_DONE = 4.5, 11.4
STATUS_BASE = [
    "●", SERVER, "DeepSeekBridge", "deepseek-chat", "default-agent",
    "sess:1a2b3c4d", "4,231/128,000", "tts:✓", "mic:✓", "sip:✓",
]

COMMANDS = [
    ("/help", "Show help: commands, shortcuts, API, docs"),
    ("/docs", "Open the online documentation in your browser"),
    ("/web", "Launch the Giraffe AI web client in the browser"),
    ("/modelsetup", "Configure LLM models & providers"),
    ("/model", "Switch the LLM provider (menu when no name)"),
    ("/agent", "Switch the agent tool set"),
    ("/voice", "Dictate from the server microphone"),
    ("/tts", "Speak the last reply via Kokoro TTS"),
    ("/new", "Start a new session (fresh conversation)"),
    ("/clear", "Reset the current session history"),
    ("/status", "Session state and platform capabilities"),
    ("/files", "Upload+attach a file, delete one, list uploads"),
    ("/attach", "Toggle a file attachment for the chat"),
    ("/shortcuts", "Keyboard shortcuts overlay"),
    ("/health", "Ping the server and report latency"),
    ("/retry", "Resend the last prompt"),
    ("/exit", "Exit the terminal UI"),
]

WELCOME = (
    "Welcome to AGENT — talk to the agents straight from the terminal.\n"
    "Type a message and press Enter · / opens commands · @ files · ? shortcuts · F1 help · F10 the menu."
)
SERVER_NOTE = f"server: {SERVER} — the API keeps answering in parallel ({SERVER}/v1/chat/completions)"


class Sim:
    def __init__(self):
        self.chat = [("· " + WELCOME, "sys"), ("· " + SERVER_NOTE, "sys")]
        self.input = ""
        self.typing = None          # (text, start, cps)
        self.stream = None          # (text, start, cps)
        self.generating = False
        self.palette = None         # {'filter': str}
        self.status_note = None
        self.ctx_hi = 128000
        self.ctx_lo = 4231


def build_events(sim):
    events = []

    def ev(t, fn):
        events.append((t, fn))

    # 1) type the first prompt (character by character)
    ev(0.8, lambda: sim.__setattr__("typing", (PROMPT, 0.8, 32.0)))

    def send_prompt():
        sim.typing = None
        sim.input = ""
        sim.chat.append(("❯ you", "you"))
        sim.chat.append((PROMPT, "you"))
        sim.generating = True
        sim.ctx_lo = 5230

    ev(3.75, send_prompt)

    # 2) stream the agent reply, word by word
    ev(STREAM_START, lambda: sim.__setattr__("stream", (REPLY, STREAM_START, 58.0)))

    def finish_stream():
        sim.stream = None
        sim.generating = False
        sim.ctx_lo = 6340
        sim.chat.append(("◆ agent", "agent"))
        sim.chat.append((REPLY, "agent"))

    ev(STREAM_DONE, finish_stream)

    # 3) /health via the command palette (live filtering)
    ev(12.2, lambda: sim.__setattr__("palette", {"filter": "/"}))

    def filt(txt):
        def _f():
            sim.palette = {"filter": txt}
        return _f

    ev(12.6, filt("/he"))
    ev(13.2, filt("/health"))

    def run_health():
        sim.palette = None
        sim.chat.append(("· server: " + SERVER + " — healthy (24 ms)", "sys"))
        sim.status_note = "healthy (24 ms)"

    ev(13.7, run_health)

    return events


# ---------------------------------------------------------------------------
# Frame rendering
# ---------------------------------------------------------------------------
def chat_lines(sim, t):
    lines = []
    for text, style in sim.chat:
        for para in text.split("\n"):
            for l in wrap(para, CHAT_INNER_W):
                lines.append((l, style))
        lines.append(("", style))          # blank separator between entries
    if sim.stream:
        text, start, cps = sim.stream
        n = max(0, min(len(text), int((t - start) * cps)))
        lines.append(("◆ agent", "agent"))
        for para in text[:n].split("\n"):
            for l in wrap(para, CHAT_INNER_W):
                lines.append((l, "agent"))
    return lines


def draw_menu(g):
    g.fill(0, 0, 0, COLS - 1, bg=MENUBG)
    items = ["File", "Chat", "Session", "Web", "Help"]
    col = 2
    for it in items:
        g.text(0, col, " " + it, fg=MENUTXT, bg=MENUBG)
        g.put(0, col + 1, it[0], fg=CYAN, bg=MENUBG, bold=True, ul=True)
        col += len(it) + 3


def draw_logo(g):
    r0, c0 = 1, 0
    r1, c1 = ROWS - 2, LOGO_W - 1
    g.frame(r0, c0, r1, c1, "AGENT")
    for i, line in enumerate(LOGO):
        g.text(r0 + 1 + i, c0 + 1, line, fg=LOGO_GRADIENT[i])
    g.text(r0 + 9, c0 + 1, "· /help  /model  /agent", fg=GRAY)


def draw_chat(g, sim, t):
    r0, c0 = 1, LOGO_W
    r1, c1 = ROWS - 2, COLS - 1
    g.frame(r0, c0, r1, c1, "Chat")

    inner_w = c1 - c0 - 1
    inner_h = r1 - r0 - 1
    x0, y0 = c0 + 1, r0 + 1

    in_text = sim.input
    if sim.typing:
        text, start, cps = sim.typing
        n = max(0, min(len(text), int((t - start) * cps)))
        in_text = text[:n]
    in_lines = wrap(in_text, inner_w) if in_text else [""]
    n_in = len(in_lines)
    view_h = inner_h - n_in

    lines = chat_lines(sim, t)
    if len(lines) > view_h:
        lines = lines[-view_h:]
    for i, (text, style) in enumerate(lines):
        fg = {"sys": GRAY, "you": WHITE, "agent": WHITE}.get(style, WHITE)
        g.text(y0 + i, x0, truncate(text, inner_w), fg=fg)

    iy = y0 + view_h
    blink = (t % 0.9) < 0.55
    if sim.typing or in_text:
        for i, line in enumerate(in_lines):
            g.text(iy + i, x0, line, fg=WHITE)
        if blink and not sim.palette:
            last = in_lines[-1]
            g.put(iy + n_in - 1, min(x0 + len(last), x0 + inner_w - 1), "█",
                  fg=WHITE, bg=WHITE)
    else:
        off = 1 if (blink and not sim.palette) else 0
        g.text(iy, x0 + off, truncate("Type a message or / for commands...", inner_w),
               fg=GRAY)
        if off:
            g.put(iy, x0, "▏", fg=GRAY, bg=GRAY)


def draw_status(g, sim, t):
    parts = list(STATUS_BASE)
    if sim.stream and sim.generating:
        frac = max(0.0, min(1.0, (t - STREAM_START) / (STREAM_DONE - STREAM_START)))
        parts[6] = f"{sim.ctx_lo + int(frac * 1100):,}/{sim.ctx_hi:,}"
    elif sim.ctx_lo:
        parts[6] = f"{sim.ctx_lo:,}/{sim.ctx_hi:,}"
    if sim.generating:
        parts.append("generating…")
    if sim.status_note:
        parts.append(sim.status_note)
    text = " · ".join(parts)
    if len(text) > 240:
        text = text[:240]
    g.text(ROWS - 1, 1, text, fg=GRAY)
    g.put(ROWS - 1, 1, "●", fg=GREEN)


def draw_palette(g, sim):
    if not sim.palette:
        return
    r0, c0 = 1, LOGO_W
    r1, c1 = ROWS - 2, COLS - 1
    dw = 48
    dh = 14
    x0 = c0 + max(0, (c1 - c0 - dw) // 2)
    y0 = r0 + 3
    x1, y1 = x0 + dw - 1, y0 + dh - 1

    g.fill(y0, x0, y1, x1, bg=BLACK)
    g.frame(y0, x0, y1, x1, "Commands")

    filt = sim.palette["filter"]
    g.text(y0 + 1, x0 + 1, truncate(filt, dw - 3), fg=WHITE)
    g.put(y0 + 1, x0 + 1 + min(len(filt), dw - 3), "█", fg=WHITE, bg=WHITE)

    items = [cmd for cmd in COMMANDS if cmd[0].startswith(filt)]
    visible = dh - 4
    for i in range(min(visible, len(items))):
        name, desc = items[i]
        line = f"{name} — {desc}"
        line = truncate(line, dw - 3)
        if i == 0:
            g.fill(y0 + 2 + i, x0 + 1, y0 + 2 + i, x1 - 1, bg=MAGENTA)
            g.text(y0 + 2 + i, x0 + 1, " " + line, fg=BLACK, bg=MAGENTA)
        else:
            g.text(y0 + 2 + i, x0 + 1, " " + line, fg=WHITE)
    g.text(y0 + dh - 2, x0 + 1, "Enter run · Tab complete · Esc cancel", fg=GRAY)


def render(sim, t, cw, chh):
    g = Grid()
    draw_menu(g)
    draw_logo(g)
    draw_chat(g, sim, t)
    draw_status(g, sim, t)
    draw_palette(g, sim)

    img = Image.new("RGB", (COLS * cw, ROWS * chh), BLACK)
    d = ImageDraw.Draw(img)
    for r in range(ROWS):
        c = 0
        while c < COLS:
            bg = g.cells[r][c].bg
            if bg != BLACK:
                c2 = c
                while c2 < COLS and g.cells[r][c2].bg == bg:
                    c2 += 1
                d.rectangle([c * cw, r * chh, c2 * cw - 1, (r + 1) * chh - 1],
                            fill=bg)
            else:
                c2 = c
                while c2 < COLS and g.cells[r][c2].bg == BLACK:
                    c2 += 1
            c = c2
    for r in range(ROWS):
        for c in range(COLS):
            cell = g.cells[r][c]
            if cell.ch != " ":
                gi = glyph(cell.ch, cell.fg, cell.bold)
                img.paste(gi, (c * cw, r * chh), gi)
                if cell.ul:
                    y = r * chh + chh - 3
                    d.line([(c * cw, y), (c * cw + cw - 1, y)], fill=cell.fg)
    return img


def chrome_size(w, h):
    pad, bar_h = 26, 34
    W, H = w + pad * 2, h + pad * 2 + bar_h
    return (W + 1 if W % 2 else W), (H + 1 if H % 2 else H)


def add_chrome(img, title, size):
    W, H = size
    pad, bar_h = 26, 34
    canvas = Image.new("RGB", (W, H), (14, 15, 19))
    d = ImageDraw.Draw(canvas)
    d.rectangle([pad - 1, pad - 1, W - pad, H - pad], outline=CHROME_EDGE, width=1)
    d.rectangle([pad, pad, W - pad - 1, pad + bar_h - 1], fill=CHROME)
    f = _font(FONT_CANDIDATES[0], 16)
    d.text((pad + 14, pad + 9), title, font=f, fill=CHROME_TXT)
    d.text((W - pad - 74, pad + 9), "–  □  ×", font=f, fill=CHROME_TXT)
    canvas.paste(img, (pad, pad + bar_h))
    return canvas


def main():
    import argparse

    ap = argparse.ArgumentParser(description="Render the AgentBridge TUI demo.")
    ap.add_argument("--fps", type=int, default=25, help="MP4 frame rate (default 25)")
    ap.add_argument("--scale", type=float, default=1.0,
                    help="MP4 scale factor vs. full resolution (default 1.0)")
    ap.add_argument("--gif-fps", type=float, default=12.5,
                    help="GIF frame rate (default 12.5)")
    ap.add_argument("--times", type=str, default=None,
                    help="storyboard mode: comma-separated sample times, e.g. 1,6,13.5,17.5")
    ap.add_argument("--duration", type=float, default=None,
                    help="storyboard mode: total duration in seconds; samples the "
                         "timeline uniformly (overrides --times)")
    ap.add_argument("--frame-ms", type=int, default=500,
                    help="storyboard mode: per-frame duration in ms (default 500)")
    args = ap.parse_args()

    sim = Sim()
    events = build_events(sim)
    fps = 25
    t_end = 19.0
    cw, chh = cell_metrics()
    print(f"cell {cw}x{chh}  grid {COLS}x{ROWS} -> {COLS * cw}x{ROWS * chh}")

    import imageio_ffmpeg

    print("ffmpeg:", imageio_ffmpeg.get_ffmpeg_exe())
    size = chrome_size(COLS * cw, ROWS * chh)
    mw, mh = int(size[0] * args.scale), int(size[1] * args.scale)
    if mw % 2:
        mw += 1
    if mh % 2:
        mh += 1
    mp4_size = (mw, mh)

    if args.duration:
        n = max(1, round(args.duration * 1000 / args.frame_ms))
        step = t_end / n
        times = [i * step for i in range(n)]
        mp4_fps = round(1000.0 / args.frame_ms, 6)
        gif_duration = args.frame_ms
        work = [(t, True, True) for t in times]
    elif args.times:
        times = [float(x) for x in args.times.split(",")]
        mp4_fps = round(1000.0 / args.frame_ms, 6)
        gif_duration = args.frame_ms
        work = [(t, True, True) for t in times]
    else:
        mp4_fps = args.fps
        gif_duration = int(round(1000 / args.gif_fps))
        gif_step = max(1, round(fps / args.gif_fps))
        mp4_stride = fps // args.fps if fps % args.fps == 0 else 1
        total = int(round(t_end * fps))
        work = [(i / fps, i % mp4_stride == 0, i % gif_step == 0)
                for i in range(total)]

    out_mp4 = os.path.join(ROOT, "demo.mp4")
    writer = imageio_ffmpeg.write_frames(
        out_mp4, mp4_size, fps=mp4_fps, pix_fmt_in="rgb24", pix_fmt_out="yuv420p",
        macro_block_size=1,
        output_params=["-preset", "medium", "-crf", "20", "-movflags", "+faststart"],
    )
    writer.send(None)

    gif_frames = []
    gif_scale = 0.5
    fired = set()
    for idx, (t, to_mp4, to_gif) in enumerate(work):
        for eidx, (te, fn) in enumerate(events):
            if eidx not in fired and te <= t:
                fired.add(eidx)
                fn()
        img = render(sim, t, cw, chh)
        framed = add_chrome(img, "agent — AGENT · AI Chat Console", size)
        if to_mp4:
            fr = framed if args.scale == 1.0 else framed.resize(mp4_size, Image.LANCZOS)
            writer.send(fr.tobytes())
        if to_gif:
            w2, h2 = int(framed.width * gif_scale), int(framed.height * gif_scale)
            gif_frames.append(
                framed.resize((w2, h2), Image.LANCZOS).quantize(colors=256,
                                                                method=Image.FASTOCTREE))
        if not args.times and idx % 50 == 0:
            print(f"frame {idx}/{len(work)}")
    writer.close()
    print(f"wrote {out_mp4}  {mp4_size}  {mp4_fps} fps")

    out_gif = os.path.join(ROOT, "demo.gif")
    gif_frames[0].save(
        out_gif, save_all=True, append_images=gif_frames[1:],
        duration=gif_duration, loop=0, optimize=True,
    )
    print(f"wrote {out_gif}  {len(gif_frames)} frames  {gif_duration} ms/frame")


if __name__ == "__main__":
    main()
