#!/usr/bin/env python3
"""ANet fork: render WinCC Unified screen JSON (from export_hmi_to_folder) into previews.

Usage:
    python3 hmi_render.py <HMI folder from export_hmi_to_folder> [--out <folder>] [--no-png]
                          [--screens "100 Home,330 Recipes"] [--no-embed]

Writes into <out> (default <HMI folder>/preview):
    <screen>.png   schematic raster preview (needs Pillow) - for a Claude session to look at
    <screen>.svg   vector preview with hover tooltips (bindings, events, navigation)
    index.html     all screens with tooltips, for a human in a browser
    screens.md     text summary: items, tag bindings, navigation targets per screen

The preview is schematic: geometry, colors, texts and bindings are real; graphics, faceplate
contents and runtime states are drawn as labelled placeholders.
"""
from __future__ import annotations

import argparse
import html
import json
import re
import sys
from pathlib import Path

NAV_PATTERN = re.compile(r'\.Screen\s*=\s*"([^"]+)"')
TAG_IN_SCRIPT = re.compile(r'Tags\(\s*"([^"]+)"\s*\)')
HTML_TAGS = re.compile(r"<[^>]+>")


# ----------------------------------------------------------------------------- helpers

def color(value, default=None):
    """'#AARRGGBB' -> (r, g, b, a) or default."""
    if not isinstance(value, str) or not re.fullmatch(r"#[0-9A-Fa-f]{8}", value):
        return default
    a, r, g, b = (int(value[i:i + 2], 16) for i in (1, 3, 5, 7))
    return (r, g, b, a)


def css(rgba):
    if rgba is None:
        return "none"
    r, g, b, a = rgba
    return f"rgba({r},{g},{b},{a / 255:.3f})"


def text_of(value) -> str:
    """Multilingual text node or plain string -> first translation without markup."""
    if isinstance(value, str):
        raw = value
    elif isinstance(value, dict) and "text" in value and not value.get("items"):
        return text_of(value.get("text"))
    elif isinstance(value, dict):
        items = value.get("items") or []
        raw = ""
        for item in items:
            if isinstance(item, dict) and item.get("text"):
                raw = item["text"]
                break
    else:
        return ""
    raw = raw.replace("</p><p>", "\n").replace("<br/>", "\n").replace("<br>", "\n")
    return html.unescape(HTML_TAGS.sub("", raw)).strip()


def num(item, key, default=0):
    value = item.get(key, default)
    return value if isinstance(value, (int, float)) else default


def bindings(item):
    result = []
    for dyn in item.get("dynamizations") or []:
        prop = dyn.get("propertyName", "?")
        if dyn.get("$type") == "TagDynamization":
            tag = dyn.get("tag") or "?"
            extra = " (indirect)" if dyn.get("useIndirectAddressing") else ""
            result.append((prop, f"tag {tag}{extra}"))
        elif "script" in dyn or dyn.get("$type", "").startswith("Script"):
            result.append((prop, "script"))
        else:
            result.append((prop, dyn.get("$type", "dynamization")))
    return result


def events(item):
    result = []
    for handler in item.get("eventHandlers") or []:
        script = handler.get("script") or {}
        code = script.get("scriptCode", "") if isinstance(script, dict) else ""
        result.append({
            "event": handler.get("eventType", "?"),
            "navigates": NAV_PATTERN.findall(code),
            "tags": sorted(set(TAG_IN_SCRIPT.findall(code))),
            "code": code.strip(),
        })
    return result


def tooltip(item):
    lines = [f"{item.get('name', '?')} [{item.get('$type', '?')}]",
             f"x={num(item, 'left')} y={num(item, 'top')} w={num(item, 'width')} h={num(item, 'height')}"]
    label = text_of(item.get("text")) or text_of(item.get("caption"))
    if label:
        lines.append(f"text: {label}")
    for prop, what in bindings(item):
        lines.append(f"{prop} <- {what}")
    for event in events(item):
        detail = []
        if event["navigates"]:
            detail.append("-> " + ", ".join(event["navigates"]))
        if event["tags"]:
            detail.append("tags " + ", ".join(event["tags"]))
        lines.append(f"on {event['event']}: " + ("; ".join(detail) if detail else "script"))
    if item.get("screenName"):
        lines.append(f"screen window: {item['screenName']}")
    return "\n".join(lines)


def bound_tag(item, prop="ProcessValue"):
    for dyn in item.get("dynamizations") or []:
        if dyn.get("propertyName") == prop and dyn.get("tag"):
            return dyn["tag"]
    return None


# ----------------------------------------------------------------------------- SVG

def svg_item(item, screens, depth, embed):
    t = item.get("$type", "")
    x, y, w, h = num(item, "left"), num(item, "top"), num(item, "width"), num(item, "height")
    if not item.get("visible", True):
        opacity = ' opacity="0.35"'
    else:
        opacity = ""
    back = color(item.get("backColor"))
    border = color(item.get("borderColor"), (100, 100, 106, 255))
    border_w = num(item, "borderWidth", 1)
    fore = color(item.get("foreColor"), (0, 0, 0, 255))
    font_px = max(9, min(18, int(h * 0.45))) if h else 12
    title = f"<title>{html.escape(tooltip(item))}</title>"
    parts = []

    def label(text, anchor="middle", color_=fore, italic=False, size=font_px, dy=0):
        if not text:
            return ""
        tx = x + w / 2 if anchor == "middle" else x + 4
        lines = text.split("\n")[:3]
        start = y + h / 2 - (len(lines) - 1) * size * 0.6 + size * 0.35 + dy
        style = "font-style:italic;" if italic else ""
        out = []
        for index, line in enumerate(lines):
            out.append(
                f'<text x="{tx:.1f}" y="{start + index * size * 1.2:.1f}" font-size="{size}" '
                f'text-anchor="{anchor}" fill="{css(color_)}" style="{style}">{html.escape(line[:80])}</text>')
        return "".join(out)

    if t == "HmiLine":
        parts.append(
            f'<line x1="{num(item, "x1")}" y1="{num(item, "y1")}" x2="{num(item, "x2")}" y2="{num(item, "y2")}" '
            f'stroke="{css(color(item.get("lineColor"), (0, 0, 0, 255)))}" stroke-width="{num(item, "lineWidth", 1)}"/>')
    elif t == "HmiText":
        parts.append(label(text_of(item.get("text")), anchor="start"))
    elif t == "HmiScreenWindow":
        name = item.get("screenName") or ""
        parts.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" fill="none" stroke="#3a6ea5" stroke-dasharray="6 4"/>')
        if embed and depth < 2 and name in screens:
            inner = screens[name]
            sw, sh = num(inner, "width", w) or w, num(inner, "height", h) or h
            scale = min(w / sw, h / sh) if sw and sh else 1
            parts.append(f'<g transform="translate({x},{y}) scale({scale:.4f})">'
                         + svg_screen_body(inner, screens, depth + 1, embed) + "</g>")
        else:
            parts.append(label(f"[window: {name}]", color_=(58, 110, 165, 255), italic=True, size=12))
    else:
        radius = 6 if t in ("HmiButton", "HmiToggleSwitch") else 0
        fill = back if back is not None else ((235, 235, 235, 255) if t != "HmiRectangle" else None)
        if t in ("HmiGraphicView", "HmiFaceplateContainer", "HmiWebControl", "HmiAlarmControl",
                 "HmiAlarmLineControl", "HmiBar", "HmiSlider"):
            parts.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" fill="{css(fill)}" '
                         f'stroke="#888" stroke-dasharray="4 3"/>')
        else:
            parts.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{radius}" fill="{css(fill)}" '
                         f'stroke="{css(border)}" stroke-width="{border_w}"/>')
        if t == "HmiButton" or t == "HmiToggleSwitch":
            parts.append(label(text_of(item.get("text"))))
        elif t == "HmiIOField":
            tag = bound_tag(item)
            io_size = max(9, min(13, font_px))
            io_text = "{" + tag + "}" if tag else "IO"
            max_chars = max(4, int((w - 6) / (io_size * 0.55)))
            if len(io_text) > max_chars:
                io_text = "…" + io_text[-(max_chars - 1):]
            parts.append(label(io_text, anchor="start", color_=(90, 90, 160, 255), italic=True, size=io_size))
        elif t == "HmiGraphicView":
            parts.append(label(f"[graphic: {item.get('graphic', '?')}]", color_=(120, 120, 120, 255), italic=True, size=12))
        elif t == "HmiFaceplateContainer":
            parts.append(label(f"[faceplate: {text_of(item.get('caption')) or item.get('name', '')}]",
                               color_=(120, 80, 20, 255), italic=True, size=12))
        elif t in ("HmiBar", "HmiSlider"):
            tag = bound_tag(item)
            parts.append(label(f"[{t[3:].lower()}: {tag or text_of(item.get('title')) or item.get('name', '')}]",
                               color_=(60, 60, 60, 255), italic=True, size=12))
        elif t in ("HmiAlarmControl", "HmiAlarmLineControl", "HmiWebControl"):
            parts.append(label(f"[{t[3:]}]", color_=(60, 60, 60, 255), italic=True, size=12))
        if bindings(item) and t != "HmiIOField":
            parts.append(f'<circle cx="{x + w - 4}" cy="{y + 4}" r="3" fill="#d9480f"/>')
    return f'<g{opacity}>{title}{"".join(parts)}</g>'


def svg_screen_body(screen, screens, depth, embed):
    w, h = num(screen, "width", 1280), num(screen, "height", 800)
    body = [f'<rect x="0" y="0" width="{w}" height="{h}" fill="{css(color(screen.get("backColor"), (255, 255, 255, 255)))}"/>']
    for item in screen.get("screenItems") or []:
        body.append(svg_item(item, screens, depth, embed))
    return "".join(body)


def svg_screen(screen, screens, embed):
    w, h = num(screen, "width", 1280), num(screen, "height", 800)
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" width="{w}" height="{h}" '
            f'font-family="Segoe UI, Lato, Arial, sans-serif">' + svg_screen_body(screen, screens, 0, embed) + "</svg>")


# ----------------------------------------------------------------------------- PNG (Pillow)

def png_screen(screen, screens, path, embed):
    from PIL import Image, ImageDraw, ImageFont

    w, h = int(num(screen, "width", 1280)), int(num(screen, "height", 800))
    image = Image.new("RGBA", (w, h), color(screen.get("backColor"), (255, 255, 255, 255)))
    draw = ImageDraw.Draw(image, "RGBA")
    font_cache = {}

    def font(size):
        if size not in font_cache:
            for candidate in ("DejaVuSans.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                              "/usr/share/fonts/truetype/lato/Lato-Regular.ttf", "arial.ttf"):
                try:
                    font_cache[size] = ImageFont.truetype(candidate, size)
                    break
                except OSError:
                    continue
            else:
                font_cache[size] = ImageFont.load_default()
        return font_cache[size]

    def draw_text(box, text, fill, size, align="center", tail=False):
        if not text:
            return
        x, y, bw, bh = box
        f = font(size)
        lines = text.split("\n")[:3]
        total = len(lines) * size * 1.2
        ty = y + (bh - total) / 2
        for line in lines:
            line = line[:80]
            if tail:
                # Keep the informative end of long tag names: "...OsaX_Pozice_Home".
                while len(line) > 4 and draw.textlength(line, font=f) > bw - 6:
                    line = "…" + line[2:]
            else:
                while len(line) > 2 and draw.textlength(line, font=f) > bw - 6:
                    line = line[:-2] + "…"
            tw = draw.textlength(line, font=f)
            tx = x + (bw - tw) / 2 if align == "center" else x + 4
            draw.text((tx, ty), line, fill=fill, font=f)
            ty += size * 1.2

    def render(scr, ox, oy, scale, depth):
        for item in scr.get("screenItems") or []:
            t = item.get("$type", "")
            x = ox + num(item, "left") * scale
            y = oy + num(item, "top") * scale
            iw, ih = num(item, "width") * scale, num(item, "height") * scale
            back = color(item.get("backColor"))
            border = color(item.get("borderColor"), (100, 100, 106, 255))
            fore = color(item.get("foreColor"), (0, 0, 0, 255))
            size = max(9, min(18, int(ih * 0.45))) if ih else 12
            if t == "HmiLine":
                draw.line([(ox + num(item, "x1") * scale, oy + num(item, "y1") * scale),
                           (ox + num(item, "x2") * scale, oy + num(item, "y2") * scale)],
                          fill=color(item.get("lineColor"), (0, 0, 0, 255)), width=max(1, int(num(item, "lineWidth", 1))))
                continue
            if t == "HmiText":
                draw_text((x, y, max(iw, 2000), ih), text_of(item.get("text")), fore, size, align="left")
                continue
            if t == "HmiScreenWindow":
                name = item.get("screenName") or ""
                if embed and depth < 2 and name in screens:
                    inner = screens[name]
                    sw, sh = num(inner, "width", iw) or iw, num(inner, "height", ih) or ih
                    s2 = min(iw / sw, ih / sh) if sw and sh else scale
                    draw.rectangle([x, y, x + sw * s2, y + sh * s2], fill=color(inner.get("backColor"), (255, 255, 255, 255)))
                    render(inner, x, y, s2, depth + 1)
                else:
                    draw.rectangle([x, y, x + iw, y + ih], outline=(58, 110, 165, 255), width=2)
                    draw_text((x, y, iw, ih), f"[window: {name}]", (58, 110, 165, 255), 12)
                continue
            placeholder = t in ("HmiGraphicView", "HmiFaceplateContainer", "HmiWebControl", "HmiAlarmControl",
                                "HmiAlarmLineControl", "HmiBar", "HmiSlider")
            fill = back if back is not None else ((235, 235, 235, 255) if t != "HmiRectangle" else None)
            if t in ("HmiButton", "HmiToggleSwitch"):
                draw.rounded_rectangle([x, y, x + iw, y + ih], radius=6, fill=fill, outline=border,
                                       width=max(1, int(num(item, "borderWidth", 1))))
                draw_text((x, y, iw, ih), text_of(item.get("text")), fore, size)
            else:
                draw.rectangle([x, y, x + iw, y + ih], fill=fill,
                               outline=(136, 136, 136, 255) if placeholder else border,
                               width=1 if placeholder else max(0, int(num(item, "borderWidth", 1))))
            if t == "HmiIOField":
                tag = bound_tag(item)
                draw_text((x, y, iw, ih), "{" + tag + "}" if tag else "IO", (90, 90, 160, 255), max(9, min(13, size)), align="left", tail=True)
            elif t == "HmiGraphicView":
                draw_text((x, y, iw, ih), f"[graphic: {item.get('graphic', '?')}]", (120, 120, 120, 255), 12)
            elif t == "HmiFaceplateContainer":
                draw_text((x, y, iw, ih), f"[faceplate: {text_of(item.get('caption')) or item.get('name', '')}]", (120, 80, 20, 255), 12)
            elif t in ("HmiBar", "HmiSlider", "HmiAlarmControl", "HmiAlarmLineControl", "HmiWebControl"):
                draw_text((x, y, iw, ih), f"[{t[3:]}: {bound_tag(item) or item.get('name', '')}]", (60, 60, 60, 255), 12)
            if bindings(item) and t != "HmiIOField":
                draw.ellipse([x + iw - 7, y + 1, x + iw - 1, y + 7], fill=(217, 72, 15, 255))

    render(screen, 0, 0, 1.0, 0)
    image.convert("RGB").save(path)


# ----------------------------------------------------------------------------- summary

def summary_md(screens):
    out = ["# HMI screens — summary", "",
           "Generated by hmi_render.py from export_hmi_to_folder. Orange dot in previews = item has a tag dynamization.", ""]
    for name in sorted(screens):
        screen = screens[name]
        items = screen.get("screenItems") or []
        out.append(f"## {name}  ({num(screen, 'width')}×{num(screen, 'height')}, {len(items)} items)")
        nav, rows = set(), []
        for item in items:
            label = text_of(item.get("text")) or text_of(item.get("caption"))
            for prop, what in bindings(item):
                rows.append(f"| {item.get('name')} | {item.get('$type', '')[3:]} | {label[:30]} | {prop} | {what} |")
            for event in events(item):
                nav.update(event["navigates"])
                if event["navigates"] or event["tags"]:
                    targets = ", ".join(event["navigates"]) or ""
                    tags = ", ".join(event["tags"]) or ""
                    rows.append(f"| {item.get('name')} | {item.get('$type', '')[3:]} | {label[:30]} | on {event['event']} | {('→ ' + targets) if targets else ''} {tags} |")
            if item.get("screenName"):
                rows.append(f"| {item.get('name')} | ScreenWindow | | Screen | {item['screenName']} |")
        if nav:
            out.append("Navigates to: " + ", ".join(sorted(nav)))
        if rows:
            out += ["", "| Item | Type | Text | Property / event | Binding |", "|---|---|---|---|---|"] + rows
        out.append("")
    return "\n".join(out)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("hmi_folder")
    parser.add_argument("--out")
    parser.add_argument("--no-png", action="store_true")
    parser.add_argument("--no-embed", action="store_true", help="do not render screens inside screen windows")
    parser.add_argument("--screens", help="comma separated screen names to render (default all)")
    args = parser.parse_args(argv)

    root = Path(args.hmi_folder)
    screen_dir = root / "Screens" if (root / "Screens").is_dir() else root
    screens = {}
    for file in sorted(screen_dir.rglob("*.json")):
        data = json.loads(file.read_text(encoding="utf-8"))
        if data.get("$type") == "HmiScreen" or "screenItems" in data:
            screens[data.get("name") or file.stem] = data
    if not screens:
        sys.exit(f"No screen JSON found under {screen_dir}")

    out = Path(args.out) if args.out else root / "preview"
    out.mkdir(parents=True, exist_ok=True)
    wanted = [s.strip() for s in args.screens.split(",")] if args.screens else sorted(screens)
    embed = not args.no_embed

    png_ok = not args.no_png
    if png_ok:
        try:
            import PIL  # noqa: F401
        except ImportError:
            print("Pillow not installed - skipping PNG", file=sys.stderr)
            png_ok = False

    index = ["<!doctype html><meta charset='utf-8'><title>HMI preview</title>",
             "<style>body{font-family:Segoe UI,Arial,sans-serif;background:#f4f4f4;margin:16px}"
             "section{margin:0 0 32px}svg{max-width:100%;height:auto;background:#fff;box-shadow:0 1px 4px #0003}"
             "nav a{margin-right:10px}</style><h1>HMI preview</h1><nav>"]
    index += [f"<a href='#s{i}'>{html.escape(n)}</a>" for i, n in enumerate(wanted)]
    index.append("</nav>")
    for i, name in enumerate(wanted):
        if name not in screens:
            print(f"skip unknown screen {name}", file=sys.stderr)
            continue
        safe = re.sub(r'[<>:"/\\|?*]', "_", name)
        svg = svg_screen(screens[name], screens, embed)
        (out / f"{safe}.svg").write_text(svg, encoding="utf-8")
        if png_ok:
            png_screen(screens[name], screens, out / f"{safe}.png", embed)
        index.append(f"<section id='s{i}'><h2>{html.escape(name)}</h2>{svg}</section>")
    (out / "index.html").write_text("".join(index), encoding="utf-8")
    (out / "screens.md").write_text(summary_md(screens), encoding="utf-8")
    print(f"rendered {len(wanted)} screens -> {out}")


if __name__ == "__main__":
    main()
