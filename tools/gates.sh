#!/bin/bash
# Usage: tools/gates.sh [flags...] ; runs player checks on the last test build, no window, no speakers.
cd "$(dirname "$0")/.."; V=$PWD/verification/test; mkdir -p $V
export PULSE_SERVER=/nonexistent
for f in ${@:--uicheck -soaktest -touchtest -audiotest}; do
  timeout -s KILL 280 Builds/test-linux/PitzutzGadol.x86_64 -batchmode -nographics $f -captureDir $V -logFile $V/${f#-}.log > /dev/null; echo "$f exit $?"
done
