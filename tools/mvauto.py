#!/usr/bin/env python3
"""
mvauto: hands-free music video processing.

Drop video files into the "video" folder next to this script. For each one, mvauto:

  1. Works out what's in it:
       * a single music video named "Artist - Song (Year)"   -> used as is
       * a compilation                                       -> split into songs
     For compilations it finds the track list by itself, trying in order:
       a. a text file next to the video with the same name (or tracks.txt)
       b. chapter titles inside the video
       c. the Internet Archive page matching the file or folder name
       d. no list at all: it cuts at the black-and-silent gaps and identifies each
          song by its sound (AcoustID; needs a free key, see SETUP below)
  2. Cuts the songs on this Mac into the Inbox.
  3. Runs mvadd: tags them, writes Jellyfin info files, copies them to the NAS
     (Media > Music Videos > Artist), and asks Jellyfin to scan.
  4. Deletes the original from "video" once everything from it is safely on the NAS.
     Anything it can't finish stays put, with the reason in video/_log.txt.

SETUP for identifying songs by sound (only needed when there's no track list):
  * brew install chromaprint
  * get a free application key at https://acoustid.org/new-application
    and save it:  echo "YOUR_KEY" > ~/.acoustid_key

Run it by double-clicking "Process Videos.command", or let the folder watcher run it
automatically (double-click "Turn On Auto Processing.command").
"""

import argparse
import datetime
import html
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import mvsplit  # noqa: E402  (cutting and gap detection)
import mvadd    # noqa: E402  (tagging, NAS copy, Jellyfin scan)

VIDEO_DIR = os.path.join(HERE, "video")
INBOX = os.path.join(HERE, "Inbox")
LOG = os.path.join(VIDEO_DIR, "_log.txt")
LOCK = os.path.join(HERE, ".mvauto.lock")
ACOUSTID_KEY_FILE = os.path.expanduser("~/.acoustid_key")
SINGLE_MAX_SECONDS = 12 * 60   # anything shorter is treated as one music video
UA = {"User-Agent": "mvauto/1.0 (personal Jellyfin helper)"}


# ---------------------------------------------------------------- small helpers

def log(msg):
    line = "{}  {}".format(datetime.datetime.now().strftime("%Y-%m-%d %H:%M"), msg)
    print(msg)
    try:
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except OSError:
        pass


def get_json(url, timeout=20):
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.load(r)


def wait_until_copied(path, settle=8, limit=3600):
    """Don't touch a file that's still being downloaded or copied in."""
    last, stable, waited = -1, 0, 0
    while waited < limit:
        try:
            size = os.path.getsize(path)
        except OSError:
            return False
        if size == last and size > 0:
            stable += 2
            if stable >= settle:
                return True
        else:
            stable = 0
        last = size
        time.sleep(2)
        waited += 2
    return False


# ---------------------------------------------------------------- track lists

def tracks_from_text(text):
    out = []
    for line in text.splitlines():
        t = mvsplit.clean_track(line)
        if t:
            out.append(t)
    return out


def sidecar_tracks(video):
    stem = os.path.splitext(video)[0]
    for cand in (stem + ".txt", stem + " - tracks.txt", os.path.join(os.path.dirname(video), "tracks.txt")):
        if os.path.isfile(cand):
            with open(cand, encoding="utf-8", errors="replace") as f:
                t = tracks_from_text(f.read())
            if t:
                return t, "the text file " + os.path.basename(cand)
    return None, None


def chapter_tracks(ffprobe, video):
    out = subprocess.run([ffprobe, "-v", "error", "-show_chapters", "-of", "json", video], capture_output=True, text=True)
    try:
        chs = json.loads(out.stdout).get("chapters", [])
    except ValueError:
        return None, None
    names = [mvsplit.clean_track((c.get("tags") or {}).get("title", "")) for c in chs]
    if chs and all(names):
        return [(float(c["start_time"]), float(c["end_time"])) for c in chs], names
    return None, None


def html_to_lines(s):
    s = re.sub(r"(?i)<\s*(br|/li|/p|/div|/h\d|/tr)\s*/?>", "\n", s)
    s = re.sub(r"<[^>]+>", "", s)
    return html.unescape(s)


def archive_candidates(video):
    stem = os.path.splitext(os.path.basename(video))[0]
    names = [stem, os.path.basename(os.path.dirname(video))]
    ids = []
    for n in names:
        for c in (n, re.sub(r"[^a-z0-9]+", "-", n.lower()).strip("-"), re.sub(r"[^A-Za-z0-9_.-]+", "_", n)):
            if c and c not in ids and c.lower() != "video":
                ids.append(c)
    return stem, ids


def archive_tracks(video):
    """Track list from the Internet Archive item this file came from."""
    stem, ids = archive_candidates(video)
    try:
        # A title search catches renamed downloads.
        q = urllib.parse.quote('title:("{}")'.format(stem.replace('"', "")))
        found = get_json("https://archive.org/advancedsearch.php?q={}&fl[]=identifier&rows=3&output=json".format(q))
        ids += [d["identifier"] for d in found.get("response", {}).get("docs", []) if d.get("identifier") not in ids]
    except Exception:
        pass
    for ident in ids:
        try:
            meta = get_json("https://archive.org/metadata/" + urllib.parse.quote(ident)).get("metadata")
        except Exception:
            continue
        if not meta:
            continue
        desc = meta.get("description", "")
        if isinstance(desc, list):
            desc = "\n".join(desc)
        t = tracks_from_text(html_to_lines(desc))
        if len(t) >= 2:
            return t, "Internet Archive item '{}'".format(ident)
    return None, None


# ---------------------------------------------------------------- identifying by sound

_last_acoustid = [0.0]


def fingerprint(ffmpeg, video, start, length):
    """(duration, fingerprint) for up to 2 minutes of audio starting at `start`."""
    tmp = tempfile.mkdtemp(prefix="mvauto")
    wav = os.path.join(tmp, "a.wav")
    try:
        subprocess.run([ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-ss", "{:.2f}".format(start), "-i", video,
                        "-t", "{:.2f}".format(min(length, 120)), "-vn", "-ac", "2", "-ar", "44100", wav],
                       capture_output=True)
        if not os.path.exists(wav):
            return None
        fpcalc = shutil.which("fpcalc") or next((p for p in ("/opt/homebrew/bin/fpcalc", "/usr/local/bin/fpcalc")
                                                 if os.path.exists(p)), None)
        if fpcalc:
            r = subprocess.run([fpcalc, "-json", wav], capture_output=True, text=True)
            if r.returncode == 0:
                return json.loads(r.stdout)["fingerprint"]
        r = subprocess.run([ffmpeg, "-nostdin", "-loglevel", "error", "-i", wav, "-f", "chromaprint",
                            "-fp_format", "base64", "-"], capture_output=True, text=True)
        return r.stdout.strip() or None
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def identify(ffmpeg, video, start, end, key):
    """Best guess 'Artist - Song (Year)' for a stretch of video, or None."""
    fp = fingerprint(ffmpeg, video, start, end - start)
    if not fp:
        return None
    wait = 0.4 - (time.time() - _last_acoustid[0])
    if wait > 0:
        time.sleep(wait)
    _last_acoustid[0] = time.time()
    data = urllib.parse.urlencode({"client": key, "meta": "recordings releases compress", "format": "json",
                                   "duration": int(end - start), "fingerprint": fp}).encode()
    try:
        req = urllib.request.Request("https://api.acoustid.org/v2/lookup", data=data, headers=UA)
        with urllib.request.urlopen(req, timeout=20) as r:
            res = json.load(r)
    except Exception:
        return None
    best = None
    for hit in res.get("results", []):
        if hit.get("score", 0) < 0.6:
            continue
        for rec in hit.get("recordings", []) or []:
            artists = [a.get("name", "") for a in rec.get("artists", []) or [] if a.get("name")]
            title = rec.get("title")
            if not artists or not title:
                continue
            years = [rl["date"]["year"] for rl in rec.get("releases", []) or []
                     if isinstance(rl.get("date"), dict) and rl["date"].get("year")]
            cand = (hit["score"], " & ".join(artists), title, min(years) if years else None)
            if best is None or cand[0] > best[0] or (cand[0] == best[0] and cand[3] and not best[3]):
                best = cand
    if not best:
        return None
    _, artist, title, year = best
    name = "{} - {}".format(artist, title) + (" ({})".format(year) if year else "")
    return re.sub(r'[\\/:*?"<>|]', "", name)


# ---------------------------------------------------------------- splitting without a list

def segments_without_list(gaps, total):
    """Cut at every gap that's both black and silent; drop short bits (intros, outros, bumpers)."""
    real = [g for g in gaps if (len(g) > 3 and g[3]) or (len(g) > 3 and g[3] is None and g[2] >= 1.0)]
    points = [0.0]
    for g in real:
        points += [g[0], g[1]]
    points.append(total)
    segs = [(points[i], points[i + 1]) for i in range(0, len(points) - 1, 2)]
    return [(a, b) for a, b in segs if b - a >= 60]


# ---------------------------------------------------------------- one file

def process(path, ffmpeg, ffprobe, key):
    name = os.path.basename(path)
    log("== " + name)
    total = mvsplit.duration(ffprobe, path)

    stem = os.path.splitext(name)[0]
    single_name = mvadd.parse(stem)

    # Look for a track list first: if there is one, it's a compilation whatever its length.
    segments, names = chapter_tracks(ffprobe, path)
    source = "the file's chapters" if segments else None
    if not segments:
        names, source = sidecar_tracks(path)
        if not names and not (single_name and total <= SINGLE_MAX_SECONDS):
            names, source = archive_tracks(path)
        if names and len(names) < 2:
            names, source = None, None
        if names:
            ch = mvsplit.chapters(ffprobe, path)
            if len(ch) == len(names):
                segments = ch
            else:
                segments = mvsplit.plan_from_gaps(mvsplit.black_gaps(ffmpeg, path), total, len(names))
                if not segments:
                    raise RuntimeError("found a {}-song list in {} but couldn't line it up with the video"
                                       .format(len(names), source))

    # No list and short: one music video.
    if not names and total <= SINGLE_MAX_SECONDS:
        if single_name:
            shutil.copyfile(path, os.path.join(INBOX, name))
            log("   single music video, named in the file name")
            return [stem]
        if key:
            guess = identify(ffmpeg, path, 0, total, key)
            if guess:
                shutil.copyfile(path, os.path.join(INBOX, guess + os.path.splitext(name)[1]))
                log("   single music video, identified by sound: " + guess)
                return [guess]
        raise RuntimeError("couldn't tell which song this is; rename it 'Artist - Song (Year)'"
                           + ("" if key else " (or set up AcoustID, see the top of mvauto.py)"))

    if names:
        log("   {} songs, track list from {}".format(len(names), source))
    else:
        if not key:
            raise RuntimeError("no track list found (no matching text file, chapters or Internet Archive page), "
                               "and identifying songs by sound isn't set up; see the top of mvauto.py")
        segments = segments_without_list(mvsplit.black_gaps(ffmpeg, path), total)
        names = []
        for i, (a, b) in enumerate(segments, 1):
            guess = identify(ffmpeg, path, a, b, key)
            names.append(guess)
            log("   song {} at {}: {}".format(i, mvsplit.fmt(a), guess or "not recognized"))
        if not any(names):
            raise RuntimeError("no track list found and none of the {} songs were recognized by sound".format(len(segments)))

    for i, ((a, b), n) in enumerate(zip(segments, names), 1):
        log("   {:>2}. {:>6} +{:>5}  {}".format(i, mvsplit.fmt(a), mvsplit.fmt(b - a), n or "(not recognized)"))

    known = [(s, n) for s, n in zip(segments, names) if n]
    unknown = [s for s, n in zip(segments, names) if not n]
    mvsplit.cut(ffmpeg, path, [s for s, _ in known], [n for _, n in known], INBOX)
    if unknown:
        held = os.path.join(INBOX, mvadd.ATTENTION)
        stem = os.path.splitext(name)[0]
        mvsplit.cut(ffmpeg, path, unknown, ["{} - part at {}".format(stem, mvsplit.fmt(a).replace(":", "m")) + "s"
                                            for a, _ in unknown], held)
        log("   {} song(s) not recognized were saved to Inbox/{} to name by hand".format(len(unknown), mvadd.ATTENTION))
    return [n for _, n in known]


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description="Process everything in the video folder.")
    ap.add_argument("--keep", action="store_true", help="don't delete originals from the video folder")
    ap.add_argument("--library", default=mvadd.LIBRARY)
    ap.add_argument("--no-scan", action="store_true")
    ap.add_argument("--watch", action="store_true", help="called by the folder watcher (quiet when there's nothing to do)")
    args = ap.parse_args()

    os.makedirs(VIDEO_DIR, exist_ok=True)
    os.makedirs(INBOX, exist_ok=True)

    # One run at a time (the watcher can fire several times while a file copies in).
    try:
        fd = os.open(LOCK, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        os.write(fd, str(os.getpid()).encode())
        os.close(fd)
    except FileExistsError:
        try:
            pid = int(open(LOCK).read().strip() or 0)
            os.kill(pid, 0)
            if not args.watch:
                print("Already running (process {}).".format(pid))
            return
        except (ValueError, ProcessLookupError, PermissionError, OSError):
            os.remove(LOCK)  # stale lock from a crash
            return main()

    try:
        ffmpeg, ffprobe = mvsplit.tool("ffmpeg"), mvsplit.tool("ffprobe")
        key = open(ACOUSTID_KEY_FILE).read().strip() if os.path.exists(ACOUSTID_KEY_FILE) else ""
        while True:
            todo = sorted(os.path.join(VIDEO_DIR, f) for f in os.listdir(VIDEO_DIR)
                          if not f.startswith((".", "_")) and os.path.splitext(f)[1].lower() in mvadd.VIDEO_EXTS
                          and os.path.isfile(os.path.join(VIDEO_DIR, f)))
            if not todo:
                if not args.watch:
                    print("Nothing to do. Drop videos into:\n  " + VIDEO_DIR)
                return
            failed, waited_out = 0, 0
            for path in todo:
                if not wait_until_copied(path):
                    waited_out += 1
                    continue
                try:
                    expected = process(path, ffmpeg, ffprobe, key)
                except Exception as e:
                    log("   NOT DONE: {}".format(e))
                    failed += 1
                    hold = os.path.join(VIDEO_DIR, "_Needs attention")
                    os.makedirs(hold, exist_ok=True)
                    shutil.move(path, os.path.join(hold, os.path.basename(path)))
                    continue

                # Tag, copy to the NAS and scan.
                cmd = [sys.executable, os.path.join(HERE, "mvadd.py"), "--inbox", INBOX, "--library", args.library,
                       "--delete-originals"]
                if args.no_scan:
                    cmd.append("--no-scan")
                subprocess.run(cmd)

                # Only delete the original if every song from it made it (added now, or already there).
                lib = mvadd.Library(args.library)
                missing = []
                for n in expected:
                    info = mvadd.parse(n) or {}
                    folder, _ = lib.artist(info.get("artist", ""))
                    if not lib.has_song(folder, info.get("title", "")):
                        missing.append(n)
                if missing:
                    log("   kept the original: {} song(s) didn't reach the NAS: {}".format(len(missing), "; ".join(missing)))
                    failed += 1
                    hold = os.path.join(VIDEO_DIR, "_Needs attention")
                    os.makedirs(hold, exist_ok=True)
                    shutil.move(path, os.path.join(hold, os.path.basename(path)))
                elif args.keep:
                    done = os.path.join(VIDEO_DIR, "_Done")
                    os.makedirs(done, exist_ok=True)
                    shutil.move(path, os.path.join(done, os.path.basename(path)))
                    log("   finished; original moved to video/_Done")
                else:
                    os.remove(path)
                    stem = os.path.splitext(path)[0]
                    for side in (stem + ".txt", stem + " - tracks.txt"):
                        if os.path.exists(side):
                            os.remove(side)
                    log("   finished; original deleted")
            if failed:
                log("Some files need attention: see video/_Needs attention and this log.")
            if waited_out == len(todo):
                return  # nothing finished copying in; the watcher will call again
    finally:
        try:
            os.remove(LOCK)
        except OSError:
            pass


if __name__ == "__main__":
    main()
