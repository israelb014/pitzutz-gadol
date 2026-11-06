#!/bin/bash
# Usage: tools/shots.sh [stage id or "battle"] ; takes real screenshots from the last test build, in a window, without sound.
cd "$(dirname "$0")/.."; V=$PWD/verification/shots; mkdir -p $V
PULSE_SERVER=/nonexistent timeout -s KILL 240 Builds/test-linux/PitzutzGadol.x86_64 -screen-fullscreen 0 -screen-width 1920 -screen-height 1080 -shots ${1:+-only $1} -captureDir $V -logFile $V/shots.log
grep -E "Missing|Exception|NullRef" $V/shots.log | sort | uniq -c | head; ls $V | wc -l
