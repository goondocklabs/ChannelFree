#!/usr/bin/env python3
"""
mvpopup: Pop-Up Video facts for music videos.

For each video it reads the song's Wikipedia article (the intro plus the Music video, Background,
Recording and similar sections) and the artist's intro, then has Claude rewrite those facts as
VH1 Pop-Up Video style bubbles, using only what the articles say. The bubbles are saved next to the
video as "<video name>.popup.txt" (one per line), which Channel Free shows during the video.

Used automatically by mvadd for new videos. To make files for videos already on the NAS:

    python3 mvpopup.py --backfill            (or double-click "Make Pop-Up Facts.command")

Existing .popup.txt files are never overwritten, so anything you write or edit yourself stays.
Needs an Anthropic API key in ~/.anthropic_key (one line). Works with the Python 3 that ships with macOS.
"""

import argparse
import json
import os
import re
import sys
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
from xml.etree import ElementTree

# ---------------------------------------------------------------- settings
LIBRARY = "/Volumes/Media/Music Videos"
API_KEY_FILE = os.path.expanduser("~/.anthropic_key")
# ...or a plain text file named anthropic-key.txt right in this folder (whichever exists).
LOCAL_KEY_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "anthropic-key.txt")
MODEL = "claude-haiku-5-5"      # cheap and quick; "claude-sonnet-5-5" writes punchier jokes for about 20x the price
BUBBLES = 8                     # how many to ask for (Channel Free shows up to its own per-video limit)
# ---------------------------------------------------------------- end of settings

VIDEO_EXTS = {".mp4", ".m4v", ".mov", ".mkv", ".avi", ".mpg", ".mpeg", ".ts", ".webm"}
UA = "ChannelFree-mvpopup/1.1 (https://github.com/goondocklabs/ChannelFree; personal music video library tool)"
WIKI = "https://en.wikipedia.org/w/api.php"
GOOD_SECTIONS = re.compile(r"music video|video|background|recording|writing|composition|production|"
                           r"release|reception|commercial|chart|legacy|in popular culture|live", re.I)
NAME_RE = re.compile(r"^(?P<artist>.+?)\s+[-–—]\s+(?P<title>.+?)(?:\s*\((?P<year>(?:19|20)\d\d)\))?(?:\s*\[[^\]]+\])?\s*$")

PROMPT = """You write the trivia bubbles for VH1's "Pop-Up Video" (1996 to 2002): short, cheeky, \
surprising facts that pop up while a music video plays. Think wry asides, behind-the-scenes gossip, \
odd numbers, and "wait, really?" details, not encyclopedia sentences.

Write up to {n} bubbles for this video:
Artist: {artist}
Song: {song}
Year: {year}

Rules:
- Use ONLY facts stated in the source text below. Never invent or guess names, numbers, places or events. \
If the sources don't support a fact, leave it out. Fewer true bubbles beat more made-up ones.
- Facts about the music video itself (director, where it was shot, cameos, props, budget) are the best. \
Then how the song was written or recorded, then chart and award trivia, then the artist.
- Each bubble is one or two short sentences, at most 110 characters. Playful tone, no profanity, no emoji.
- Don't start with the song or artist name every time; vary it.
- Best bubble first.

Answer with only a JSON array of strings, like ["first bubble", "second bubble"]. \
Answer [] if the sources have nothing usable about this song or artist.

Source text:
{sources}"""


def key(s):
    s = unicodedata.normalize("NFKD", s.replace("’", "'"))
    return re.sub(r"[^a-z0-9]", "", s.lower())


def plain_song(title):
    s = re.sub(r"\s*[\(\[][^\)\]]*[\)\]]", "", title).strip()
    return s or title


_last_wiki = [0.0]


def http_json(url, data=None, headers=None, tries=3):
    wiki = "wikipedia.org" in url
    if wiki:
        tries = 6
    for attempt in range(tries):
        if wiki:
            # Wikipedia asks scripts to go easy: at most about one request a second.
            wait = _last_wiki[0] + 1.2 - time.time()
            if wait > 0:
                time.sleep(wait)
            _last_wiki[0] = time.time()
        try:
            req = urllib.request.Request(url, data=data, headers=dict({"User-Agent": UA}, **(headers or {})))
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.loads(r.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            if e.code in (429, 500, 502, 503, 529) and attempt < tries - 1:
                try:
                    pause = float(e.headers.get("Retry-After") or 0)
                except ValueError:
                    pause = 0
                time.sleep(max(pause, 10 * (attempt + 1) if e.code == 429 else 3 * (attempt + 1)))
                continue
            detail = ""
            try:
                detail = json.loads(e.read().decode("utf-8")).get("error", {}).get("message", "")
            except Exception:
                pass
            hint = {401: " (the API key isn't accepted: check anthropic-key.txt has just the key, pasted whole)",
                    400: "", 403: " (this key isn't allowed to do that)"}.get(e.code, "")
            raise RuntimeError("HTTP {} from {}: {}{}".format(e.code, urllib.parse.urlparse(url).netloc, detail or e.reason, hint)) from None
        except (urllib.error.URLError, TimeoutError):
            if attempt < tries - 1:
                time.sleep(3 * (attempt + 1))
                continue
            raise


def wiki_search(query):
    q = urllib.parse.urlencode({"action": "query", "list": "search", "format": "json", "formatversion": 2,
                                "srlimit": 5, "srsearch": query})
    res = http_json(WIKI + "?" + q).get("query", {}).get("search", [])
    return [r["title"] for r in res if "discography" not in r["title"].lower() and not r["title"].startswith("List of")]


def wiki_text(title, intro_only):
    params = {"action": "query", "prop": "extracts", "explaintext": 1, "redirects": 1, "format": "json",
              "formatversion": 2, "titles": title}
    if intro_only:
        params["exintro"] = 1
    pages = http_json(WIKI + "?" + urllib.parse.urlencode(params)).get("query", {}).get("pages", [])
    return pages[0].get("extract", "") if pages else ""


def mentions(text, words):
    return any(re.search(r"\b" + re.escape(w) + r"s?\b", text[:400], re.I) for w in words)


def song_sources(artist, song):
    want = key(plain_song(song))
    for title in wiki_search("{} {} song".format(plain_song(song), artist))[:4]:
        if want not in key(title):
            continue
        full = wiki_text(title, intro_only=False)
        if not mentions(full, ["song", "single", "track", "ballad"]):
            continue
        # Intro + the sections Pop-Up Video would mine; skip track listings, personnel, references.
        parts = re.split(r"\n(={2,}\s*[^=\n]+?\s*={2,})\n", full)
        keep, heading = [parts[0]], None
        for p in parts[1:]:
            m = re.match(r"={2,}\s*([^=]+?)\s*={2,}$", p)
            if m:
                heading = m.group(1)
            elif heading and GOOD_SECTIONS.search(heading) and not re.search(r"track|personnel|list|reference|note", heading, re.I):
                keep.append("[{}] {}".format(heading, p.strip()))
        text = "\n\n".join(x for x in keep if x.strip())
        return title, text[:12000]
    return None, ""


def artist_sources(artist):
    want = key(artist)
    for title in wiki_search("{} band singer musician".format(artist))[:4]:
        if not key(title).startswith(want):
            continue
        intro = wiki_text(title, intro_only=True)
        if mentions(intro, ["band", "singer", "rapper", "musician", "songwriter", "duo", "group", "DJ", "producer", "trio"]):
            return title, intro[:3000]
    return None, ""


def ask_claude(api_key, artist, song, year, sources):
    body = json.dumps({
        "model": MODEL,
        "max_tokens": 3000,
        "messages": [{"role": "user", "content": PROMPT.format(n=BUBBLES, artist=artist, song=song, year=year or "unknown", sources=sources)}],
    }).encode("utf-8")
    res = http_json("https://api.anthropic.com/v1/messages", data=body, headers={
        "x-api-key": api_key, "anthropic-version": "2023-06-01", "content-type": "application/json"})
    text = "".join(b.get("text", "") for b in res.get("content", []) if b.get("type") == "text").strip()
    m = re.search(r"\[.*\]", text, re.S)
    bubbles = json.loads(m.group(0)) if m else []
    out = []
    for b in bubbles:
        b = re.sub(r"\s+", " ", str(b)).strip()
        if 10 <= len(b) <= 140:
            out.append(b)
    return out


def api_key():
    for f in (LOCAL_KEY_FILE, API_KEY_FILE):
        try:
            k = open(f).read().strip()
            if k:
                return k
        except OSError:
            pass
    return ""


def facts_for(artist, song, year, log=print):
    """Bubbles for one video, or [] (no key, nothing on Wikipedia, or a network problem)."""
    k = api_key()
    if not k:
        return []
    s_title, s_text = song_sources(artist, song)
    a_title, a_text = artist_sources(artist)
    if not s_text and not a_text:
        log("    pop-up: nothing on Wikipedia for this one")
        return []
    sources = ""
    if s_text:
        sources += "=== Wikipedia: {} ===\n{}\n\n".format(s_title, s_text)
    if a_text:
        sources += "=== Wikipedia: {} ===\n{}\n".format(a_title, a_text)
    return ask_claude(k, artist, song, year, sources)


def popup_path(video_path):
    return os.path.splitext(video_path)[0] + ".popup.txt"


def write_popup(path, bubbles, artist, song):
    with open(path, "w", encoding="utf-8") as f:
        f.write("# Pop-Up Video facts for {} - {} (from Wikipedia, written up by Claude). Edit freely; one per line.\n".format(artist, song))
        for b in bubbles:
            f.write(b + "\n")


def info_from(video_path):
    """Artist, song, year from the .nfo next to the video, else from the file name."""
    nfo = os.path.splitext(video_path)[0] + ".nfo"
    if os.path.exists(nfo):
        try:
            root = ElementTree.parse(nfo).getroot()
            artist = (root.findtext("artist") or "").strip()
            title = (root.findtext("title") or "").strip()
            year = (root.findtext("year") or "").strip()
            if artist and title:
                return artist, title, year
        except ElementTree.ParseError:
            pass
    m = NAME_RE.match(os.path.splitext(os.path.basename(video_path))[0])
    if m:
        return m.group("artist").strip(), m.group("title").strip(), m.group("year") or ""
    return None


def backfill(library, limit=None):
    if not api_key():
        sys.exit("Put your Anthropic API key in anthropic-key.txt in this folder first (just the key, one line).")
    if not os.path.isdir(library):
        sys.exit("Can't reach {}. Open Finder > Network > WOPR > Media once, then try again.".format(library))
    videos = []
    for root, dirs, files in os.walk(library):
        dirs[:] = [d for d in dirs if not d.startswith(".")]
        for f in sorted(files):
            if not f.startswith(".") and os.path.splitext(f)[1].lower() in VIDEO_EXTS:
                videos.append(os.path.join(root, f))
    todo = [v for v in sorted(videos) if not os.path.exists(popup_path(v))]
    print("{} music videos, {} still need Pop-Up facts.".format(len(videos), len(todo)))
    made = empty = failed = 0
    for i, v in enumerate(todo[:limit] if limit else todo, 1):
        info = info_from(v)
        name = os.path.relpath(v, library)
        if not info:
            print("[{}/{}] {}: can't tell the artist and song, skipped".format(i, len(todo), name))
            continue
        artist, song, year = info
        try:
            bubbles = facts_for(artist, song, year, log=lambda s: None)
        except Exception as e:  # network trouble: leave it for the next run
            if "HTTP 401" in str(e) or "HTTP 403" in str(e) or "credit balance" in str(e).lower():
                sys.exit(str(e))
            print("[{}/{}] {}: failed ({}), will retry next run".format(i, len(todo), name, e))
            failed += 1
            continue
        if bubbles:
            write_popup(popup_path(v), bubbles, artist, song)
            made += 1
            print("[{}/{}] {}: {} bubbles".format(i, len(todo), name, len(bubbles)))
        else:
            # An empty file (just the comment line) so it isn't looked up again; Channel Free then tries Wikipedia itself.
            write_popup(popup_path(v), [], artist, song)
            empty += 1
            print("[{}/{}] {}: nothing on Wikipedia".format(i, len(todo), name))
    print("\nDone. Made {}, nothing found for {}, failed {}.".format(made, empty, failed))
    if empty:
        print("Videos with nothing found play without bubbles; write a .popup.txt for any you like.")


def main():
    ap = argparse.ArgumentParser(description="Make Pop-Up Video facts for music videos.")
    ap.add_argument("--backfill", action="store_true", help="make facts for every video on the NAS that doesn't have them")
    ap.add_argument("--library", default=LIBRARY)
    ap.add_argument("--limit", type=int, help="only do this many (handy for a test run)")
    ap.add_argument("--show", nargs=2, metavar=("ARTIST", "SONG"), help="print the bubbles for one song without saving")
    args = ap.parse_args()
    if args.show:
        try:
            bubbles = facts_for(args.show[0], args.show[1], "")
        except RuntimeError as e:
            sys.exit(str(e))
        if not api_key():
            sys.exit("No API key yet: put it in anthropic-key.txt in this folder (just the key, one line).")
        for b in bubbles:
            print("* " + b)
        if not bubbles:
            print("No bubbles (nothing usable on Wikipedia for that song).")
    elif args.backfill:
        backfill(args.library, args.limit)
    else:
        ap.print_help()


if __name__ == "__main__":
    main()
