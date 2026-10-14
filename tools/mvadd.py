#!/usr/bin/env python3
"""
mvadd: add music videos to the Jellyfin Music Videos library in one step.

Drop video files into the inbox folder, named like:

    Artist - Song (Year).mp4
    Artist - Song (Year) [Genre].mp4
    Artist - Song.mp4            (the year is looked up on MusicBrainz)

then run this script (or double-click "Add Music Videos.command"). For each file it:

  1. reads the artist, song, year and optional genre from the file name
  2. looks up a missing year on MusicBrainz
  3. writes those tags into the video and an .nfo file Jellyfin reads
  4. saves it as  Music Videos/Artist/Artist - Song (Year).mp4
  5. moves the original into the inbox's _Processed folder
and finally tells Jellyfin to scan for the new videos.

Files it can't handle go into the inbox's _Needs attention folder with the reason printed.
Works with the Python 3 that ships with macOS. Needs ffmpeg (brew install ffmpeg).
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.parse
import urllib.request
from xml.sax.saxutils import escape

# ---------------------------------------------------------------- settings
INBOX = "/Volumes/Media/Music Video Inbox"
LIBRARY = "/Volumes/Media/Music Videos"
JELLYFIN_URL = "http://192.168.4.117:8096"
# Jellyfin API key: Dashboard > API Keys > +. Put it in ~/.mvadd_key (one line) so it isn't in this file.
KEY_FILE = os.path.expanduser("~/.mvadd_key")
LOOKUP_MISSING_YEARS = True
KEEP_ORIGINALS = True  # False = delete originals after they're safely in the library
# ---------------------------------------------------------------- end of settings

VIDEO_EXTS = {".mp4", ".m4v", ".mov", ".mkv", ".avi", ".mpg", ".mpeg", ".vob", ".ts", ".m2ts", ".wmv", ".flv", ".webm"}
NAME_RE = re.compile(
    r"^(?P<artist>.+?)\s+[-–—]\s+(?P<title>.+?)"
    r"(?:\s*\((?P<year>(?:19|20)\d\d)\))?"
    r"(?:\s*\[(?P<genre>[^\]]+)\])?\s*$"
)
PROCESSED = "_Processed"
ATTENTION = "_Needs attention"


def find_ffmpeg():
    for cand in (shutil.which("ffmpeg"), "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg"):
        if cand and os.path.exists(cand):
            return cand
    return None


def safe(name):
    """Make a string safe for a file or folder name on the NAS and on Macs."""
    name = re.sub(r'[\\/:*?"<>|]', "", name).strip().strip(".")
    return re.sub(r"\s+", " ", name) or "Unknown"


def parse(stem):
    m = NAME_RE.match(stem.replace("_", " ").strip())
    if not m:
        return None
    return {
        "artist": m.group("artist").strip(),
        "title": m.group("title").strip(),
        "year": m.group("year"),
        "genre": (m.group("genre") or "").strip() or None,
    }


_last_lookup = [0.0]


def lookup_year(artist, title):
    """Earliest release year of the song on MusicBrainz, or None. Polite: one request per second."""
    wait = 1.1 - (time.time() - _last_lookup[0])
    if wait > 0:
        time.sleep(wait)
    _last_lookup[0] = time.time()
    q = 'recording:"{}" AND artist:"{}"'.format(title.replace('"', ""), artist.replace('"', ""))
    url = "https://musicbrainz.org/ws/2/recording?fmt=json&limit=15&query=" + urllib.parse.quote(q)
    req = urllib.request.Request(url, headers={"User-Agent": "mvadd/1.0 (personal Jellyfin helper)"})
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            data = json.load(r)
    except Exception:
        return None
    years = []
    for rec in data.get("recordings", []):
        if int(rec.get("score", 0)) < 90:
            continue
        d = rec.get("first-release-date") or ""
        if re.match(r"^(19|20)\d\d", d):
            years.append(int(d[:4]))
    return str(min(years)) if years else None


def write_nfo(path, info):
    lines = ["<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>", "<musicvideo>",
             "  <title>{}</title>".format(escape(info["title"])),
             "  <artist>{}</artist>".format(escape(info["artist"])),
             "  <year>{}</year>".format(info["year"]),
             "  <premiered>{}-01-01</premiered>".format(info["year"])]
    if info.get("genre"):
        lines.append("  <genre>{}</genre>".format(escape(info["genre"])))
    lines.append("</musicvideo>")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def tag_copy(ffmpeg, src, dst, info):
    """Copy into an .mp4 with tags. Tries a fast copy first, re-encodes only if the format needs it."""
    meta = ["-metadata", "title=" + info["title"], "-metadata", "artist=" + info["artist"],
            "-metadata", "album_artist=" + info["artist"], "-metadata", "date=" + info["year"]]
    if info.get("genre"):
        meta += ["-metadata", "genre=" + info["genre"]]
    base = [ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", src,
            "-map", "0:v:0", "-map", "0:a:0?", "-map_metadata", "-1"]
    tail = meta + ["-movflags", "+faststart", dst]
    fast = subprocess.run(base + ["-c", "copy"] + tail, capture_output=True, text=True)
    if fast.returncode == 0 and os.path.getsize(dst) > 0:
        return "copied"
    slow = subprocess.run(base + ["-c:v", "libx264", "-crf", "18", "-preset", "medium", "-pix_fmt", "yuv420p",
                                  "-c:a", "aac", "-b:a", "192k"] + tail, capture_output=True, text=True)
    if slow.returncode == 0 and os.path.getsize(dst) > 0:
        return "converted"
    if os.path.exists(dst):
        os.remove(dst)
    raise RuntimeError((slow.stderr or fast.stderr or "ffmpeg failed").strip().splitlines()[-1])


def move_aside(path, inbox, folder, note=None):
    target_dir = os.path.join(inbox, folder)
    os.makedirs(target_dir, exist_ok=True)
    target = os.path.join(target_dir, os.path.basename(path))
    n = 2
    while os.path.exists(target):
        stem, ext = os.path.splitext(os.path.basename(path))
        target = os.path.join(target_dir, "{} ({}){}".format(stem, n, ext))
        n += 1
    shutil.move(path, target)
    if note:
        with open(os.path.join(target_dir, "_why.txt"), "a", encoding="utf-8") as f:
            f.write("{}: {}\n".format(os.path.basename(target), note))


def jellyfin_scan(url, key):
    req = urllib.request.Request(url.rstrip("/") + "/Library/Refresh", method="POST", data=b"",
                                 headers={"Authorization": 'MediaBrowser Token="{}"'.format(key)})
    with urllib.request.urlopen(req, timeout=15) as r:
        return r.status


def main():
    ap = argparse.ArgumentParser(description="Add music videos to Jellyfin.")
    ap.add_argument("--inbox", default=INBOX)
    ap.add_argument("--library", default=LIBRARY)
    ap.add_argument("--no-lookup", action="store_true", help="don't look up missing years online")
    ap.add_argument("--no-scan", action="store_true", help="don't ask Jellyfin to scan afterwards")
    ap.add_argument("--dry-run", action="store_true", help="show what would happen, change nothing")
    args = ap.parse_args()

    ffmpeg = find_ffmpeg()
    if not ffmpeg:
        sys.exit("ffmpeg isn't installed. Run:  brew install ffmpeg")
    for label, folder in (("Inbox", args.inbox), ("Library", args.library)):
        if not os.path.isdir(folder):
            sys.exit("{} folder not found: {}\nIs the NAS share mounted in Finder?".format(label, folder))

    files = sorted(f for f in os.listdir(args.inbox)
                   if os.path.isfile(os.path.join(args.inbox, f)) and not f.startswith(".")
                   and os.path.splitext(f)[1].lower() in VIDEO_EXTS)
    if not files:
        print("Inbox is empty. Drop videos into:\n  " + args.inbox)
        return

    added, attention, looked_up = [], [], []
    for i, name in enumerate(files, 1):
        src = os.path.join(args.inbox, name)
        stem = os.path.splitext(name)[0]
        print("[{}/{}] {}".format(i, len(files), name))
        info = parse(stem)
        if not info:
            reason = "name isn't 'Artist - Song (Year)'"
            print("    skipped: " + reason)
            attention.append((name, reason))
            if not args.dry_run:
                move_aside(src, args.inbox, ATTENTION, reason)
            continue

        if not info["year"] and LOOKUP_MISSING_YEARS and not args.no_lookup:
            info["year"] = lookup_year(info["artist"], info["title"])
            if info["year"]:
                looked_up.append("{} - {} ({})".format(info["artist"], info["title"], info["year"]))
                print("    found year on MusicBrainz: " + info["year"])
        if not info["year"]:
            reason = "no year in the name and none found online; rename it with (Year)"
            print("    skipped: " + reason)
            attention.append((name, reason))
            if not args.dry_run:
                move_aside(src, args.inbox, ATTENTION, reason)
            continue

        artist_dir = os.path.join(args.library, safe(info["artist"]))
        out_stem = safe("{} - {} ({})".format(info["artist"], info["title"], info["year"]))
        dst = os.path.join(artist_dir, out_stem + ".mp4")
        if os.path.exists(dst):
            reason = "already in the library as " + os.path.relpath(dst, args.library)
            print("    skipped: " + reason)
            attention.append((name, reason))
            if not args.dry_run:
                move_aside(src, args.inbox, ATTENTION, reason)
            continue

        if args.dry_run:
            print("    would add: " + os.path.relpath(dst, args.library))
            continue

        os.makedirs(artist_dir, exist_ok=True)
        tmp = os.path.join(artist_dir, "." + out_stem + ".partial.mp4")
        try:
            how = tag_copy(ffmpeg, src, tmp, info)
            os.replace(tmp, dst)
            write_nfo(os.path.join(artist_dir, out_stem + ".nfo"), info)
        except Exception as e:
            if os.path.exists(tmp):
                os.remove(tmp)
            reason = "couldn't process the video: {}".format(e)
            print("    failed: " + reason)
            attention.append((name, reason))
            move_aside(src, args.inbox, ATTENTION, reason)
            continue

        print("    added ({}): {}".format(how, os.path.relpath(dst, args.library)))
        added.append(out_stem)
        if KEEP_ORIGINALS:
            move_aside(src, args.inbox, PROCESSED)
        else:
            os.remove(src)

    print("\n---- Summary ----")
    print("Added: {}".format(len(added)))
    if looked_up:
        print("Years looked up online (worth a glance):")
        for s in looked_up:
            print("  " + s)
    if attention:
        print("Needs attention ({}), moved to '{}':".format(len(attention), ATTENTION))
        for n, why in attention:
            print("  {}: {}".format(n, why))

    if added and not args.no_scan and not args.dry_run:
        key = open(KEY_FILE).read().strip() if os.path.exists(KEY_FILE) else ""
        if not key:
            print("\nNo Jellyfin API key in {}, so run 'Scan All Libraries' in Jellyfin yourself.".format(KEY_FILE))
        else:
            try:
                jellyfin_scan(JELLYFIN_URL, key)
                print("\nJellyfin is scanning for the new videos. Channels pick them up within about 15 minutes,"
                      "\nor click Rebuild schedule in Channel Free.")
            except Exception as e:
                print("\nCouldn't reach Jellyfin to start a scan ({}). Run 'Scan All Libraries' yourself.".format(e))


if __name__ == "__main__":
    main()
