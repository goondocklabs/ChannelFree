#!/bin/zsh
# Double-click: process everything in the Inbox on this Mac, copy the finished videos to the NAS,
# and tell Jellyfin to scan.
cd "$(dirname "$0")"
/usr/bin/env python3 "$(dirname "$0")/mvadd.py"
echo
read "?Done. Press Return to close this window."
