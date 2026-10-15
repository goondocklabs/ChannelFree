#!/bin/zsh
# Double-click to stop processing the "video" folder automatically.
LABEL="com.goondock.mvauto"
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null
rm -f "$HOME/Library/LaunchAgents/$LABEL.plist"
echo "Auto processing is OFF. You can still double-click Process Videos.command."
echo
read "?Press Return to close this window."
