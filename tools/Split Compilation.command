#!/bin/zsh
# Double-click in Finder: split a music video compilation into separate videos,
# then tag them and add them to Jellyfin.
cd "$(dirname "$0")"
/usr/bin/env python3 "$(dirname "$0")/mvsplit.py" --out "$(dirname "$0")"
echo
read "?Press Return to close this window."
