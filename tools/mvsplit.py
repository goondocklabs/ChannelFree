#!/usr/bin/env python3
"""
mvsplit: split a music video compilation into one file per video.

    python3 mvsplit.py "Compilation.mp4" tracks.txt
    (or double-click "Split Compilation.command" and follow the prompts)

tracks.txt is the track list in order, one per line, the way sites usually show it:

    1. Celine Dion - My Heart Will Go On
    2. Mariah Carey - Hero (1993)

How it finds the cuts:
  * If the file has one chapter per track, it uses the chapters.
  * Otherwise it scans for the short black screens between videos and picks the
    set of gaps that best fits the number of tracks (longer black gaps and normal
    song lengths win; dark scenes inside a video lose).

It shows the plan and asks before cutting. If a cut is off, answer "n" and it saves
the plan as a text file you can edit and run again with --cuts.
All cutting happens on this Mac. The finished files go into the Inbox next to this
script, named "Artist - Song (Year).mp4", then mvadd tags them and copies them to the NAS.
"""

import argparse
import json
import math
import os
import re
import shutil
import subprocess
import sys

INBOX = os.path.join(os.path.dirname(os.path.abspath(__file__)), "Inbox")  # on this Mac, next to the tools

TRACK_RE = re.compile(r"^\s*(?:\d+\s*[.)\-:]\s*)?(?P<name>.+?)\s*$")


def tool(name):
    for cand in (shutil.which(name), "/opt/homebrew/bin/" + name, "/usr/local/bin/" + name):
        if cand and os.path.exists(cand):
            return cand
    sys.exit("{} isn't installed. Run:  brew install ffmpeg".format(name))


STAMP_RE = re.compile(r"^\s*[\(\[]?(?P<ts>(?:\d{1,2}:)?\d{1,2}:\d{2})[\)\]]?\s*(?:[-\u2010-\u2015|.:]\s*)?")
TRAILING_STAMP_RE = re.compile(r"\s*[\(\[]?(?:\d{1,2}:)?\d{1,2}:\d{2}[\)\]]?\s*$")


def stamp_seconds(line):
    """Seconds from a timestamp at the start of a track line ("3:45 Artist - Song", "1:02:10 - ..."), else None."""
    m = STAMP_RE.match(line)
    if not m:
        return None
    parts = [int(p) for p in m.group("ts").split(":")]
    secs = 0
    for p in parts:
        secs = secs * 60 + p
    return float(secs)


QUOTED_RE = re.compile(r'^(?P<artist>.+?)\s*(?:[-\u2010-\u2015:]\s*)?["\u201c\u201d](?P<title>[^"\u201c\u201d]+)["\u201c\u201d]\s*(?P<rest>.*)$')
FILLER_RE = re.compile(r"\b(promo|ident|bumper|commercials?|adverts?|intro|outro|station id|interlude|break)\b", re.I)


def is_filler(line):
    """A track list line that isn't a song: '(promo) VH1 loading to MTV 00s', '(welcome to MTV 00s) promo'."""
    text = STAMP_RE.sub("", line, count=1).strip()
    return bool(FILLER_RE.search(text)) and (text.startswith("(") or " - " not in text and '"' not in text and "\u201c" not in text)


def clean_track(line):
    line = STAMP_RE.sub("", line, count=1)
    line = TRAILING_STAMP_RE.sub("", line)
    q = QUOTED_RE.match(line.strip())
    if q:  # Artist "Song"  ->  Artist - Song
        line = "{} - {}{}".format(q.group("artist").strip(), q.group("title").strip(),
                                  (" " + q.group("rest").strip()) if q.group("rest").strip() else "")
    m = TRACK_RE.match(line)
    if not m:
        return None
    name = m.group("name").strip().strip('"')
    name = name.replace("\u00a0", " ")
    name = re.sub(r"\s+[\u2010-\u2015\u2212\ufe58\ufe63\uff0d]\s+", " - ", name)  # any dash style -> " - "
    name = re.sub(r"\s+-{2,}\s+", " - ", name)
    name = name.replace("...", "").replace("…", "")
    name = re.sub(r'[\\/:*?"<>|]', "", name)
    name = re.sub(r"\s+", " ", name).strip(" .")
    return name if " - " in name else None


def read_tracks(path):
    tracks, bad = [], []
    with open(path, encoding="utf-8") as f:
        for line in f:
            if not line.strip():
                continue
            t = clean_track(line)
            (tracks if t else bad).append(t or line.strip())
    return tracks, bad


def duration(ffprobe, video):
    out = subprocess.run([ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "json", video],
                         capture_output=True, text=True)
    return float(json.loads(out.stdout)["format"]["duration"])


def chapters(ffprobe, video):
    out = subprocess.run([ffprobe, "-v", "error", "-show_chapters", "-of", "json", video], capture_output=True, text=True)
    try:
        return [(float(c["start_time"]), float(c["end_time"])) for c in json.loads(out.stdout).get("chapters", [])]
    except Exception:
        return []


def black_gaps(ffmpeg, video):
    """Black screens, each marked True when the sound also drops out (a real gap, not a dark scene)."""
    print("Scanning for the gaps between videos (a minute or two)...")
    # d=0.03 also catches one-frame black flashes, which is sometimes the only break between two videos.
    out = subprocess.run([ffmpeg, "-nostdin", "-hide_banner", "-i", video,
                          "-vf", "blackdetect=d=0.03:pix_th=0.10:pic_th=0.90", "-af", "silencedetect=n=-40dB:d=0.25",
                          "-f", "null", "-"], capture_output=True, text=True)
    gaps = []
    for m in re.finditer(r"black_start:([\d.]+) black_end:([\d.]+) black_duration:([\d.]+)", out.stderr):
        gaps.append((float(m.group(1)), float(m.group(2)), float(m.group(3))))
    starts = [float(x) for x in re.findall(r"silence_start: (-?[\d.]+)", out.stderr)]
    ends = [float(x) for x in re.findall(r"silence_end: ([\d.]+)", out.stderr)]
    silences = list(zip(starts, ends + [float("inf")] * (len(starts) - len(ends))))
    return with_silence_breaks(mark_quiet(gaps, silences), silences)


def with_silence_breaks(gaps, silences, min_len=1.5):
    """Long silences with no black screen are also possible breaks (videos that cut straight to the next one)."""
    extra = [(s0, s1, 0.0, True) for s0, s1 in silences
             if s1 != float("inf") and s1 - s0 >= min_len
             and not any(g[0] - 1.0 <= s1 and g[1] + 1.0 >= s0 for g in gaps)]
    return sorted(gaps + extra)


def mark_quiet(gaps, silences):
    """Add a 4th field: True if a silence overlaps the black gap (give or take a second)."""
    if not silences:
        return [g + (None,) for g in gaps]  # no silence info: judge on black alone
    return [g + (any(s0 <= g[1] + 1.0 and s1 >= g[0] - 1.0 for s0, s1 in silences),) for g in gaps]


def plan_from_gaps(gaps, total, n):
    """Pick n segments. Boundaries: content start (0 or a gap in the first 45 s), n-1 gaps, content end."""
    starts = [(0.0, None)] + [(g[1], g) for g in gaps if g[1] <= 45]          # (time, gap)
    ends = [(total, None)] + [(g[0], g) for g in gaps if g[0] >= total - 75]
    inner = [g for g in gaps if 45 < g[1] and g[0] < total - 20]

    def seg_cost(length):
        if length < 60:
            return 50.0
        if length > 600:
            return 50.0
        # songs are usually 2:30 to 5:30; mild preference for ~4 minutes
        return 0.15 * abs(math.log(length / 240.0)) * 10 + (0 if 120 <= length <= 400 else 4)

    def cut_reward(gap):
        black, quiet = gap[2], gap[3] if len(gap) > 3 else None
        r = min(black, 2.5) * 2.0  # longer black = more likely a real change
        if quiet is True:
            r += 8.0   # black AND silent: almost certainly between two videos
        elif quiet is False:
            r -= 6.0   # black but the music keeps going: a fade inside a video
        return r

    best = None
    for s_time, s_black in starts:
        # dp over inner gaps: dp[k][j] = best cost with k cuts, last cut at inner[j]
        cands = [g for g in inner if g[1] > s_time + 60]
        INF = float("inf")
        dp = [[INF] * len(cands) for _ in range(n)]
        back = [[-1] * len(cands) for _ in range(n)]
        for j, g in enumerate(cands):
            dp[1 % n][j] = seg_cost(g[0] - s_time) - cut_reward(g) if n > 1 else INF
        for k in range(2, n):
            for j, g in enumerate(cands):
                for i in range(j):
                    if dp[k - 1][i] == INF:
                        continue
                    c = dp[k - 1][i] + seg_cost(g[0] - cands[i][1]) - cut_reward(g)
                    if c < dp[k][j]:
                        dp[k][j], back[k][j] = c, i
        for e_time, e_black in ends:
            if n == 1:
                cost = seg_cost(e_time - s_time)
                path = []
            else:
                cost, last = INF, -1
                for j, g in enumerate(cands):
                    if dp[n - 1][j] == INF or g[1] >= e_time - 60:
                        continue
                    c = dp[n - 1][j] + seg_cost(e_time - g[1])
                    if c < cost:
                        cost, last = c, j
                if last < 0:
                    continue
                path, k, j = [], n - 1, last
                while j >= 0 and k >= 1:
                    path.append(cands[j])
                    j, k = back[k][j], k - 1
                path.reverse()
            # trimming an intro/outro at a real gap is usually right
            cost -= (cut_reward(s_black) / 2 if s_black else 0) + (cut_reward(e_black) / 2 if e_black else 0)
            if best is None or cost < best[0]:
                best = (cost, s_time, path, e_time)
    if best is None:
        return None
    _, s_time, path, e_time = best
    points = [s_time] + [x for g in path for x in (g[0], g[1])] + [e_time]
    return [(points[i], points[i + 1]) for i in range(0, len(points), 2)]


def fmt(t):
    return "{}:{:02d}".format(int(t // 60), int(t % 60))


def interlaced(ffmpeg, video):
    """True for interlaced video (typical of DVD .VOB files), which needs deinterlacing to avoid comb lines."""
    probe = os.path.join(os.path.dirname(ffmpeg), "ffprobe")
    if not os.path.exists(probe):
        probe = shutil.which("ffprobe") or "ffprobe"
    try:
        out = subprocess.run([probe, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=field_order",
                              "-of", "default=nw=1:nk=1", video], capture_output=True, text=True).stdout.strip()
    except OSError:
        out = ""
    if out in ("tt", "bb", "tb", "bt"):
        return True
    return out != "progressive" and os.path.splitext(video)[1].lower() in (".vob", ".mpg", ".mpeg", ".ts", ".m2ts")


def cut(ffmpeg, video, segments, names, outdir):
    os.makedirs(outdir, exist_ok=True)
    deint = ["-vf", "bwdif=mode=send_frame"] if interlaced(ffmpeg, video) else []
    if deint:
        print("  (interlaced source: deinterlacing)")
    for (a, b), name in zip(segments, names):
        dst = os.path.join(outdir, name + ".mp4")
        print("  cutting: " + name)
        r = subprocess.run([ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-fflags", "+genpts",
                            "-ss", "{:.3f}".format(a), "-i", video, "-t", "{:.3f}".format(b - a),
                            "-map", "0:v:0", "-map", "0:a:0?"] + deint +
                           ["-c:v", "libx264", "-crf", "18", "-preset", "fast", "-pix_fmt", "yuv420p",
                            "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", dst], capture_output=True, text=True)
        if r.returncode != 0:
            print("    failed: " + (r.stderr.strip().splitlines() or ["ffmpeg error"])[-1])


def ask(outdir):
    """Interactive mode for the double-click launcher."""
    print("=== Split a music video compilation ===\n")
    raw = input("1) Drag the compilation video into this window, then press Return:\n> ").strip()
    path = re.sub(r"\\(.)", r"\1", raw.strip("'\"")).strip()
    if not os.path.isfile(os.path.expanduser(path)):
        sys.exit("Can't find that file: " + path)
    print("\n2) Paste the track list (one 'Artist - Song' per line, in order).")
    print("   Press Return on an empty line when you're done:")
    lines = []
    while True:
        try:
            line = input()
        except EOFError:
            break
        if not line.strip():
            if lines:
                break
            continue
        lines.append(line)
    tracks = os.path.splitext(os.path.expanduser(path))[0] + " - tracks.txt"
    with open(tracks, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print()
    return path, tracks


def main():
    ap = argparse.ArgumentParser(description="Split a music video compilation into separate videos.")
    ap.add_argument("video", nargs="?", help="the compilation (leave out to be asked)")
    ap.add_argument("tracks", nargs="?", help="text file with the track list, in order")
    ap.add_argument("--out", default=INBOX, help="where the finished videos go (default: the Music Video Inbox)")
    ap.add_argument("--cuts", help="use an edited plan file instead of detecting")
    ap.add_argument("--yes", action="store_true", help="don't ask before cutting")
    ap.add_argument("--add", action="store_true", help="run mvadd afterwards to tag and add them to Jellyfin")
    args = ap.parse_args()

    ffmpeg, ffprobe = tool("ffmpeg"), tool("ffprobe")
    if not args.video:
        args.video, args.tracks = ask(args.out)
        args.add = True
    video = os.path.abspath(os.path.expanduser(args.video))
    if not os.path.isfile(video):
        sys.exit("Can't find the video: " + video)
    os.makedirs(args.out, exist_ok=True)

    if args.cuts:
        segments, names = [], []
        with open(args.cuts, encoding="utf-8") as f:
            for line in f:
                if not line.strip() or line.lstrip().startswith("#"):
                    continue
                a, b, name = line.rstrip("\n").split("|", 2)
                segments.append((float(a), float(b)))
                names.append(name.strip())
    else:
        if not args.tracks:
            sys.exit("Give the track list file too (or --cuts with a plan file).")
        names, bad = read_tracks(args.tracks)
        if bad:
            print("These lines don't look like 'Artist - Song' and were skipped:")
            for b in bad:
                print("  " + b)
        if not names:
            sys.exit("No tracks found in " + args.tracks)
        total = duration(ffprobe, video)
        ch = chapters(ffprobe, video)
        if len(ch) == len(names):
            print("Using the file's {} chapters.".format(len(ch)))
            segments = ch
        else:
            gaps = black_gaps(ffmpeg, video)
            segments = plan_from_gaps(gaps, total, len(names))
            if not segments:
                sys.exit("Couldn't find {} videos' worth of gaps in this file ({} black gaps found). "
                         "Check the track list matches the video.".format(len(names), len(gaps)))

    print("\nPlan ({} videos):".format(len(segments)))
    print("   #  start   length  name")
    for i, ((a, b), name) in enumerate(zip(segments, names), 1):
        flag = "   <- unusual length, check this one" if not 120 <= b - a <= 420 else ""
        print("  {:>2}  {:>6}  {:>6}  {}{}".format(i, fmt(a), fmt(b - a), name, flag))

    plan_file = os.path.splitext(video)[0] + " - cuts.txt"
    if not args.yes:
        ans = input("\nCut it like this? [y/n] ").strip().lower()
        if ans != "y":
            with open(plan_file, "w", encoding="utf-8") as f:
                f.write("# start seconds | end seconds | name. Edit the numbers, save, then run:\n")
                f.write("#   python3 mvsplit.py \"{}\" --cuts \"{}\"\n".format(video, plan_file))
                for (a, b), name in zip(segments, names):
                    f.write("{:.2f}|{:.2f}|{}\n".format(a, b, name))
            print("Saved the plan to:\n  {}\nFix the times in it and run again with --cuts.".format(plan_file))
            return

    print("\nCutting into: " + args.out)
    cut(ffmpeg, video, segments, names, args.out)
    print("Done. Your original file wasn't changed.")

    if args.add:
        here = os.path.dirname(os.path.abspath(__file__))
        mvadd = os.path.join(here, "mvadd.py")
        if os.path.exists(mvadd):
            print("\nAdding them to Jellyfin...\n")
            subprocess.run([sys.executable, mvadd, "--inbox", args.out])
        else:
            print("mvadd.py isn't next to this script; double-click Add Music Videos.command to finish.")


if __name__ == "__main__":
    main()
