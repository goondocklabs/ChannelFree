#!/bin/zsh
# Double-click: process everything in the "video" folder right now.
cd "$(dirname "$0")"
/usr/bin/env python3 "$(dirname "$0")/mvauto.py"
echo
read "?Done. Press Return to close this window."
