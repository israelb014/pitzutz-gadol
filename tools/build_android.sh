#!/bin/bash
# Usage: tools/build_android.sh ; the password is read from outside the repository.
cd "$(dirname "$0")/.."
set -a; . ~/work/_secrets/pitzutz-gadol-android.env; set +a
tools/run.sh Android ${1:-BuildAndroid} -buildTarget Android; r=$?
unset PITZUTZ_KEYSTORE_PASS
exit $r
