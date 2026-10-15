#!/usr/bin/env python3
"""
mvadd: add music videos to the Jellyfin Music Videos library in one step.

Everything is processed on this Mac, in the folder this script lives in; only finished
videos are copied to the NAS. Drop video files into the Inbox folder next to this script, named like:

    Artist - Song (Year).mp4
    Artist - Song (Year) [Genre].mp4
    Artist - Song.mp4            (the year is looked up on MusicBrainz)

then run this script (or double-click "Add Music Videos.command"). For each file it:

  1. reads the artist, song, year and optional genre from the file name
  2. looks up a missing year on MusicBrainz
  3. writes those tags into the video and an .nfo file Jellyfin reads
  4. copies the finished file to the NAS as  Music Videos/Artist/Artist - Song (Year).mp4
  5. moves the original into the Inbox's _Processed folder
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
import difflib
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
from xml.sax.saxutils import escape

# ---------------------------------------------------------------- settings
WORKSHOP = os.path.dirname(os.path.abspath(__file__))   # the folder these tools live in, on your Mac
INBOX = os.path.join(WORKSHOP, "Inbox")
STAGING = os.path.join(WORKSHOP, ".working")
LIBRARY = "/Volumes/Media/Music Videos"                  # on the NAS
NAS_SHARE = "smb://192.168.4.117/Media"                  # mounted automatically if it isn't already
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


JUNK_RE = re.compile(r"\s*[\(\[][^\)\]]*\b(official|music video|video|remaster(ed)?|hd|hq|4k|lyrics?|explicit|clean|audio)\b[^\)\]]*[\)\]]",
                     re.IGNORECASE)


def match_key(s):
    """Spelling-proof key: 'Beyoncé' = 'beyonce', 'The Police' = 'Police', '&' = 'and', punctuation ignored."""
    s = unicodedata.normalize("NFKD", s)
    s = "".join(c for c in s if not unicodedata.combining(c)).lower()
    s = s.replace("&", " and ").replace("+", " and ")
    s = re.sub(r"^\s*the\s+", "", s)
    return re.sub(r"[^a-z0-9]", "", s)


def song_key(title):
    """Same song even with '(Official Video)', '[HD]', 'Remastered' and so on tacked on."""
    return match_key(JUNK_RE.sub("", title))


class Library:
    """What's already on the NAS, so new videos join existing artist folders and duplicates are caught."""

    def __init__(self, root):
        self.root = root
        self.artists = {}  # match key -> (folder name, artist spelling used in its tags)
        if os.path.isdir(root):
            for folder in sorted(os.listdir(root)):
                path = os.path.join(root, folder)
                if os.path.isdir(path) and not folder.startswith((".", "_", "#")):
                    self.artists.setdefault(match_key(folder), (folder, self._tag_spelling(path) or folder))

    @staticmethod
    def _tag_spelling(folder):
        for f in sorted(os.listdir(folder)):
            if f.endswith(".nfo"):
                try:
                    m = re.search(r"<artist>(.*?)</artist>", open(os.path.join(folder, f), encoding="utf-8").read())
                except OSError:
                    m = None
                if m:
                    return m.group(1).replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">")
        return None

    def artist(self, name):
        """(folder, artist spelling) to use. Reuses an existing folder when the name matches one."""
        key = match_key(name)
        if key in self.artists:
            return self.artists[key]
        entry = (safe(name), name)
        self.artists[key] = entry
        return entry

    def has_song(self, folder, title):
        """Existing file in the artist folder that's the same song, or None."""
        path = os.path.join(self.root, folder)
        if not os.path.isdir(path):
            return None
        want = song_key(title)
        for f in os.listdir(path):
            stem, ext = os.path.splitext(f)
            if ext.lower() not in VIDEO_EXTS or f.startswith("."):
                continue
            info = parse(stem)
            if info and song_key(info["title"]) == want:
                return f
        return None


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


ARTIST_SPLIT = re.compile(r"\s*(?:,|&|\+|\band\b|\bfeat\.?|\bft\.?|\bfeaturing\b|\bwith\b|\bx\b|\bvs\.?)\s*", re.IGNORECASE)


def _similar(a, b):
    return difflib.SequenceMatcher(None, match_key(a), match_key(b)).ratio()


def _plain_title(t):
    """Title without bracketed extras: 'Mambo No. 5 (A Little Bit Of) (Ver. 2)' -> 'Mambo No. 5'."""
    return re.sub(r"\s*[\(\[][^\)\]]*[\)\]]", "", t).strip() or t


def _mb_search(query):
    url = "https://musicbrainz.org/ws/2/recording?fmt=json&limit=25&query=" + urllib.parse.quote(query)
    for attempt in range(4):
        wait = 1.1 - (time.time() - _last_lookup[0])
        if wait > 0:
            time.sleep(wait)
        _last_lookup[0] = time.time()
        req = urllib.request.Request(url, headers={"User-Agent": "mvadd/1.1 (personal Jellyfin helper)"})
        try:
            with urllib.request.urlopen(req, timeout=20) as r:
                return json.load(r).get("recordings", [])
        except urllib.error.HTTPError as e:
            if e.code not in (429, 500, 502, 503, 504):
                return []
        except Exception:
            pass
        time.sleep(2 * (attempt + 1))  # busy: back off and retry
    return []


def lookup(artist, title):
    """(year, corrected artist spelling or None) from MusicBrainz. Tolerates typos, extra notes in the
    title and several artists in one name. Polite: about one request per second."""
    artists = [x for x in ARTIST_SPLIT.split(artist) if len(match_key(x)) >= 2] or [artist]
    plain = _plain_title(title)

    def terms(text, fuzzy):
        words = [w for w in re.findall(r"[A-Za-z0-9']+", text) if len(w) >= 3 or w.isdigit()]
        words = [w.replace("'", "") for w in words]
        return " OR ".join(("artist:{}~".format(w) if fuzzy else "artist:{}".format(w)) for w in words)

    def clean(t):
        return t.replace('"', "").replace("\\", "")

    queries = [
        'recording:"{}" AND artist:"{}"'.format(clean(title), clean(artist)),
        'recording:"{}" AND ({})'.format(clean(plain), terms(" ".join(artists), True)),
        'recording:({}) AND ({})'.format(" ".join(re.findall(r"[A-Za-z0-9]+", plain)), terms(" ".join(artists), True)),
    ]
    for q in queries:
        hits = []
        for rec in _mb_search(q):
            if int(rec.get("score", 0)) < 60:
                continue
            credit = "".join(c.get("name", "") + c.get("joinphrase", "") for c in rec.get("artist-credit", []) or [])
            if _similar(_plain_title(rec.get("title", "")), plain) < 0.8:
                continue
            artist_ok = max([_similar(credit, artist)] + [_similar(c.get("name", ""), x)
                                                           for c in rec.get("artist-credit", []) or [] for x in artists])
            if artist_ok < 0.75:
                continue
            d = rec.get("first-release-date") or ""
            if re.match(r"^(19|20)\d\d", d):
                hits.append((int(d[:4]), int(rec.get("score", 0)), credit))
        if hits:
            year = min(h[0] for h in hits)
            best_credit = max(hits, key=lambda h: h[1])[2]
            sim = _similar(best_credit, artist)
            best_credit = best_credit.replace("\u2019", "'").replace("\u2018", "'")
            fix = best_credit if best_credit and 0.8 <= sim < 1.0 and match_key(best_credit) != match_key(artist) else None
            return str(year), fix
    return None, None


def lookup_year(artist, title):
    return lookup(artist, title)[0]


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


def ensure_nas(library):
    """Mount the NAS share if needed (uses the password saved in your Keychain)."""
    if os.path.isdir(library):
        return True
    print("Connecting to the NAS...")
    try:
        subprocess.run(["osascript", "-e", 'mount volume "{}"'.format(NAS_SHARE)], capture_output=True)
    except OSError:
        return False
    for _ in range(20):
        if os.path.isdir(library):
            return True
        time.sleep(0.5)
    return False


def copy_to_nas(src, dst):
    """Copy to the NAS under a temporary name, check the size, then rename into place."""
    tmp = os.path.join(os.path.dirname(dst), "." + os.path.basename(dst) + ".copying")
    shutil.copyfile(src, tmp)
    if os.path.getsize(tmp) != os.path.getsize(src):
        os.remove(tmp)
        raise RuntimeError("copy to the NAS came out the wrong size")
    os.replace(tmp, dst)


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
    ap.add_argument("--delete-originals", action="store_true", help="delete inbox files once they're on the NAS")
    args = ap.parse_args()

    ffmpeg = find_ffmpeg()
    if not ffmpeg:
        sys.exit("ffmpeg isn't installed. Run:  brew install ffmpeg")
    os.makedirs(args.inbox, exist_ok=True)
    if not args.dry_run and not ensure_nas(args.library):
        sys.exit("Can't reach the Music Videos folder on the NAS:\n  {}\n"
                 "Open Finder > Network > WOPR > Media once, then run this again.".format(args.library))

    files = sorted(f for f in os.listdir(args.inbox)
                   if os.path.isfile(os.path.join(args.inbox, f)) and not f.startswith(".")
                   and os.path.splitext(f)[1].lower() in VIDEO_EXTS)
    if not files:
        print("The Inbox is empty. Drop videos into:\n  " + args.inbox)
        return

    library = Library(args.library)
    added, attention, looked_up, merged, fixed = [], [], [], [], []
    # First pass: names and years for everything, so missing years can borrow from the rest of the batch.
    infos, guessed = {}, []
    for name in files:
        info = parse(os.path.splitext(name)[0])
        infos[name] = info
        if info and not info["year"] and LOOKUP_MISSING_YEARS and not args.no_lookup:
            print("Looking up the year for " + name)
            year, fix = lookup(info["artist"], info["title"])
            if year:
                info["year"] = year
                looked_up.append("{} - {} ({})".format(info["artist"], info["title"], year))
            if fix:
                fixed.append("{} -> {}".format(info["artist"], fix))
                info["artist"] = fix
    known = sorted(int(v["year"]) for v in infos.values() if v and v["year"])
    if len(known) >= 3 and known[-1] - known[0] <= 12:
        typical = str(known[len(known) // 2])
        for name, info in infos.items():
            if info and not info["year"]:
                info["year"] = typical
                guessed.append("{} - {} ({})".format(info["artist"], info["title"], typical))

    for i, name in enumerate(files, 1):
        src = os.path.join(args.inbox, name)
        stem = os.path.splitext(name)[0]
        print("[{}/{}] {}".format(i, len(files), name))
        info = infos[name]
        if not info:
            reason = "name isn't 'Artist - Song (Year)'"
            print("    skipped: " + reason)
            attention.append((name, reason))
            if not args.dry_run:
                move_aside(src, args.inbox, ATTENTION, reason)
            continue

        if not info["year"]:
            reason = "no year in the name and none found online; rename it with (Year)"
            print("    skipped: " + reason)
            attention.append((name, reason))
            if not args.dry_run:
                move_aside(src, args.inbox, ATTENTION, reason)
            continue

        folder, spelling = library.artist(info["artist"])
        if spelling != info["artist"]:
            print("    using the existing artist folder '{}'".format(folder))
            merged.append("{} -> {}".format(info["artist"], spelling))
            info["artist"] = spelling  # same spelling in the tags, so Jellyfin shows one artist
        artist_dir = os.path.join(args.library, folder)
        out_stem = safe("{} - {} ({})".format(info["artist"], info["title"], info["year"]))
        dst = os.path.join(artist_dir, out_stem + ".mp4")
        existing = library.has_song(folder, info["title"])
        if existing or os.path.exists(dst):
            reason = "already in the library as " + os.path.join(folder, existing or os.path.basename(dst))
            print("    skipped: " + reason)
            attention.append((name, reason))
            if not args.dry_run:
                move_aside(src, args.inbox, ATTENTION, reason)
            continue

        if args.dry_run:
            print("    would add: " + os.path.relpath(dst, args.library))
            continue

        stage_dir = os.path.join(STAGING, safe(info["artist"]))
        os.makedirs(stage_dir, exist_ok=True)
        local_mp4 = os.path.join(stage_dir, out_stem + ".mp4")
        local_nfo = os.path.join(stage_dir, out_stem + ".nfo")
        try:
            how = tag_copy(ffmpeg, src, local_mp4, info)        # done on this Mac
            write_nfo(local_nfo, info)
            os.makedirs(artist_dir, exist_ok=True)
            copy_to_nas(local_mp4, dst)                          # then just the finished files go over
            copy_to_nas(local_nfo, os.path.join(artist_dir, out_stem + ".nfo"))
        except Exception as e:
            for leftover in (local_mp4, local_nfo):
                if os.path.exists(leftover):
                    os.remove(leftover)
            reason = "couldn't process the video: {}".format(e)
            print("    failed: " + reason)
            attention.append((name, reason))
            move_aside(src, args.inbox, ATTENTION, reason)
            continue

        for done in (local_mp4, local_nfo):
            os.remove(done)
        try:
            os.rmdir(stage_dir)
        except OSError:
            pass
        print("    added ({}): {}".format(how, os.path.relpath(dst, args.library)))
        added.append(out_stem)
        if KEEP_ORIGINALS and not args.delete_originals:
            move_aside(src, args.inbox, PROCESSED)
        else:
            os.remove(src)

    print("\n---- Summary ----")
    print("Added: {}".format(len(added)))
    if merged:
        print("Matched to artists you already have:")
        for m in sorted(set(merged)):
            print("  " + m)
    if fixed:
        print("Artist names corrected to the MusicBrainz spelling:")
        for f in fixed:
            print("  " + f)
    if guessed:
        print("No year found anywhere, so these got the typical year of the rest of the batch (worth a check):")
        for g in guessed:
            print("  " + g)
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
