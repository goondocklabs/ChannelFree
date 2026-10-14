#!/bin/zsh
# Double-click in Finder to add everything in this inbox to the Music Videos library.
cd "$(dirname "$0")"
/usr/bin/env python3 "$(dirname "$0")/mvadd.py" --inbox "$(dirname "$0")" "$@"
echo
read "?Done. Press Return to close this window."
