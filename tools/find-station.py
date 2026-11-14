#!/usr/bin/env python3
"""
find-station.py — discover a radio station's now-playing endpoint for stations.json.

Usage:
    python3 tools/find-station.py <station-website-or-endpoint-or-stream-url> [--match "Name"]

What it does, from a single URL:
  1. If it's an audio stream, reads ICY StreamTitle directly.
  2. Fetches the page and scans the HTML + same-origin JS for now-playing URLs.
  3. Probes common endpoints (AzuraCast / Icecast / Shoutcast / WP themes).
  4. Fetches each candidate, shows the response, classifies it (json / delimited),
     and prints a ready-to-paste stations.json entry for the best match.

Pure standard library — no pip install needed.
"""
import sys, re, json, argparse
from urllib.parse import urljoin, urlparse
from urllib.request import Request, urlopen

UA = "Mozilla/5.0 (OpenMediaBridge find-station)"
TIMEOUT = 8

# URL fragments that usually point at a now-playing feed.
KEYWORDS = ["now-playing", "now_playing", "nowplaying", "currentsong", "current-song",
            "checklaatst", "laatstgespeeld", "playlist", "metadata", "status-json",
            "/stats", "nowplaying.json", "track", "/song"]

# Endpoints worth trying relative to scheme://host even if nothing links them.
COMMON = ["/api/public/now-playing", "/api/nowplaying", "/status-json.xsl",
          "/stats?json=1", "/currentsong", "/nowplaying.json", "/7.html"]


def fetch(url, headers=None, limit=200_000):
    req = Request(url, headers={"User-Agent": UA, **(headers or {})})
    with urlopen(req, timeout=TIMEOUT) as r:
        return r.status, dict(r.headers), r.read(limit)


def try_icy(url):
    """Read one ICY StreamTitle from an audio stream, or return None."""
    try:
        req = Request(url, headers={"User-Agent": UA, "Icy-MetaData": "1"})
        with urlopen(req, timeout=TIMEOUT) as r:
            interval = int(r.headers.get("icy-metaint", 0))
            if interval <= 0:
                return None
            r.read(interval)                    # skip one audio block
            length = r.read(1)
            if not length:
                return None
            meta = r.read(length[0] * 16).decode("utf-8", "replace")
            m = re.search(r"StreamTitle='([^']*)'", meta)
            return m.group(1).strip() if m else None
    except Exception:
        return None


def scan_for_urls(base, text):
    found = set()
    for m in re.findall(r"""["'`]([^"'`\s]*?)["'`]""", text):
        low = m.lower()
        if any(k in low for k in KEYWORDS) and not low.endswith((".css", ".png", ".jpg", ".svg", ".woff", ".woff2")):
            try:
                found.add(urljoin(base, m))
            except Exception:
                pass
    return found


def walk_json(obj, path=""):
    """Yield (dot.path, value) for string/number leaves; indexes into lists."""
    if isinstance(obj, dict):
        for k, v in obj.items():
            yield from walk_json(v, f"{path}.{k}" if path else k)
    elif isinstance(obj, list):
        for i, v in enumerate(obj):
            yield from walk_json(v, f"{path}.{i}" if path else str(i))
    elif isinstance(obj, (str, int, float)):
        yield path, obj


def looks_like_markup(text):
    head = text[:500].lstrip().lower()
    return head.startswith(("<!doctype", "<?xml", "<html")) or "<html" in head or "<script" in head


def classify(text):
    """Return a dict describing how to parse this body, or None."""
    text = text.strip()
    if not text or looks_like_markup(text):
        return None
    # JSON?
    try:
        data = json.loads(text)
        paths = list(walk_json(data))
        # Pick the best leaf matching one of `names`: earlier names rank higher,
        # and a path under song/now_playing is strongly preferred (so the track
        # "title" beats a station "name").
        def pick(names):
            best, best_score = "", -1
            for p, v in paths:
                if not isinstance(v, str) or not v.strip():
                    continue
                leaf = p.split(".")[-1].lower()
                if leaf not in names:
                    continue
                score = len(names) - names.index(leaf)
                if any(x in p.lower() for x in ("now_playing", "nowplaying", "song", "playing")):
                    score += 10
                if score > best_score:
                    best, best_score = p, score
            return best
        art = pick(["artist", "artist_name", "performer"])
        tit = pick(["title", "track", "song", "name"])
        cov = pick(["art", "cover", "coverurl", "image", "artwork", "thumbnail"])
        if art or tit:
            return {"type": "json", "artistPath": art, "titlePath": tit, "coverPath": cov,
                    "preview": {p: v for p, v in paths[:12]}}
        return None
    except Exception:
        pass
    # Delimited text? Guess separators.
    sec_sep = "#" if "#" in text else ""
    sections = text.split(sec_sep) if sec_sep else [text]
    field_sep = ";" if ";" in text else ("|" if "|" in text else ("\t" if "\t" in text else ""))
    if not field_sep:
        return None
    mid = len(sections) // 2  # "now" is often the middle of prev/now/next
    fields = sections[mid].split(field_sep)
    # Reject anything that smells like markup/code rather than clean metadata.
    if len(fields) < 2 or any("<" in f or ">" in f or "{" in f or len(f) > 150 for f in fields[:3]):
        return None
    return {"type": "delimited", "sectionSep": sec_sep, "section": mid,
            "fieldSep": field_sep, "artistField": 0, "titleField": 1,
            "coverField": 2 if len(fields) > 2 else -1,
            "preview": {"sections": [s[:60] for s in sections], "now_fields": fields}}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("url")
    ap.add_argument("--match", default="", help="station name to put in the entry")
    args = ap.parse_args()

    start = args.url if "://" in args.url else "https://" + args.url
    base = f"{urlparse(start).scheme}://{urlparse(start).netloc}"
    print(f"→ Investigating {start}\n")

    # 1. ICY stream?
    icy = try_icy(start)
    if icy:
        print(f"ICY StreamTitle: {icy!r}")
        print("  → this stream already exposes metadata; a dedicated player (mpv/VLC)")
        print("    would forward it via MPRIS, no stations.json entry needed.\n")

    # 2. Fetch page, collect candidate URLs from HTML + same-origin JS.
    candidates = set()
    try:
        status, hdrs, body = fetch(start)
        ctype = hdrs.get("Content-Type", "")
        text = body.decode("utf-8", "replace")
        if "html" in ctype or "<html" in text.lower():
            candidates |= scan_for_urls(start, text)
            for js in re.findall(r'<script[^>]+src=["\']([^"\']+)["\']', text):
                src = urljoin(start, js)
                if urlparse(src).netloc == urlparse(start).netloc:
                    try:
                        _, _, jb = fetch(src)
                        candidates |= scan_for_urls(src, jb.decode("utf-8", "replace"))
                    except Exception:
                        pass
        else:
            # URL was itself an endpoint, not a page.
            candidates.add(start)
    except Exception as e:
        print(f"  (couldn't fetch page: {e})")

    # 3. Add common patterns.
    candidates |= {urljoin(base, c) for c in COMMON}

    # 4. Probe each candidate.
    ASSET_EXT = (".js", ".css", ".woff", ".woff2", ".png", ".jpg", ".jpeg",
                 ".svg", ".gif", ".ico", ".map")
    STRONG = ["checklaatst", "laatstgespeeld", "nowplaying", "now-playing",
              "now_playing", "currentsong", "current-song", "status-json",
              "7.html", "nowplaying.json"]
    print(f"Testing {len(candidates)} candidate endpoint(s):\n")
    scored = []
    for url in sorted(candidates):
        if url.lower().split("?")[0].endswith(ASSET_EXT):
            continue  # assets hold the endpoint reference, aren't the data
        try:
            st, _, b = fetch(url, limit=20_000)
        except Exception:
            continue  # 404/timeout — skip quietly
        body_text = b.decode("utf-8", "replace")
        spec = classify(body_text) if st == 200 else None
        snippet = body_text.strip().replace("\n", " ")[:120]
        print(f"  {st} {('[' + spec['type'] + ']') if spec else '[?]'} {url}")
        print(f"       {snippet}")
        if spec:
            score = (100 if any(k in url.lower() for k in STRONG) else 0)
            score += 20 if spec["type"] == "json" else 10
            score -= min(len(body_text) // 500, 20)  # prefer compact responses
            scored.append((score, url, spec))

    top = max(scored, default=None)
    best = (top[1], top[2]) if top else None

    # 5. Suggest an entry.
    if best:
        url, spec = best
        preview = spec.pop("preview", None)
        entry = {"match": args.match or "STATION NAME", "url": url, **spec,
                 "pollSeconds": 10, "enabled": True}
        print("\n✅ Best candidate — suggested stations.json entry:\n")
        print(json.dumps(entry, indent=2, ensure_ascii=False))
        if preview:
            print("\n(parsed preview — check artist/title fields are right:)")
            print(json.dumps(preview, indent=2, ensure_ascii=False))
        print("\nAdjust the field indexes / paths if the artist and title are swapped,")
        print('then paste this into the "stations" array in stations.json.')
    else:
        print("\n⚠ No parseable now-playing endpoint found automatically.")
        print("  Open the station's player in a browser, watch the Network tab for a")
        print("  repeating request (every ~10s), and add that URL manually.")


if __name__ == "__main__":
    main()
