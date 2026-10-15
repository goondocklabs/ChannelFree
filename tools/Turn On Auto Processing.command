#!/bin/zsh
# Double-click once: from now on, anything dropped into the "video" folder is processed automatically
# (also checked every 10 minutes as a safety net). Survives restarts.
HERE="$(cd "$(dirname "$0")" && pwd)"
LABEL="com.goondock.mvauto"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
PY="$(command -v python3 || echo /usr/bin/python3)"
mkdir -p "$HERE/video" "$HERE/.logs" "$HOME/Library/LaunchAgents"
cat > "$PLIST" <<PL
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>Label</key><string>$LABEL</string>
  <key>ProgramArguments</key><array>
    <string>$PY</string><string>$HERE/mvauto.py</string><string>--watch</string>
  </array>
  <key>WatchPaths</key><array><string>$HERE/video</string></array>
  <key>StartInterval</key><integer>600</integer>
  <key>EnvironmentVariables</key><dict>
    <key>PATH</key><string>/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin</string>
  </dict>
  <key>StandardOutPath</key><string>$HERE/.logs/auto.log</string>
  <key>StandardErrorPath</key><string>$HERE/.logs/auto.log</string>
</dict></plist>
PL
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null
launchctl bootstrap "gui/$(id -u)" "$PLIST" && echo "Auto processing is ON. Drop videos into:\n  $HERE/video\nProgress is written to video/_log.txt"
echo
read "?Press Return to close this window."
