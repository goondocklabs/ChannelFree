#!/bin/zsh
# Double-click: make Pop-Up Video facts for every music video on the NAS that doesn't have them yet.
# Safe to run again any time; it skips videos that already have facts and never overwrites your edits.
cd "$(dirname "$0")"
/usr/bin/env python3 "$(dirname "$0")/mvpopup.py" --backfill
echo
read "?Done. Press Return to close this window."
