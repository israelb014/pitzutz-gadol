#!/bin/bash
# Usage: tools/package.sh ; packs the three release files into Builds/release.
cd "$(dirname "$0")/.."; R=Builds/release; mkdir -p $R; rm -f $R/*
tar czf $R/PitzutzGadol-linux.tar.gz --exclude='*_BurstDebugInformation_DoNotShip' --exclude='*_BackUpThisFolder_ButDontShipItWithYourGame' -C Builds linux
python3 - <<'PY'
import os, zipfile
with zipfile.ZipFile('Builds/release/PitzutzGadol-windows.zip', 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for root, dirs, files in os.walk('Builds/windows'):
        if 'DoNotShip' in root or 'DontShip' in root: continue
        for f in files:
            p = os.path.join(root, f); z.write(p, os.path.relpath(p, 'Builds'))
PY
cp Builds/android/PitzutzGadol-android.apk $R/
ls -la $R
