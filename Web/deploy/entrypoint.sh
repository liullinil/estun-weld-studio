#!/bin/sh
set -eu
godot --headless --path /app/native res://Tests/WebBridge.tscn &
planner_pid=$!
node /app/web/server/server.mjs &
web_pid=$!
trap 'kill "$planner_pid" "$web_pid" 2>/dev/null || true; wait || true' TERM INT EXIT
while kill -0 "$planner_pid" 2>/dev/null && kill -0 "$web_pid" 2>/dev/null; do sleep 2; done
exit 1
