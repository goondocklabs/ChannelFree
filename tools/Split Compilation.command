#!/bin/zsh
# Double-click: split a music video compilation on this Mac, then tag the videos
# and copy them to the NAS.
cd "$(dirname "$0")"
/usr/bin/env python3 "$(dirname "$0")/mvsplit.py"
echo
read "?Press Return to close this window."
