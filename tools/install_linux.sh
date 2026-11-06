#!/bin/bash
# Usage: tools/install_linux.sh ; installs the Linux build to ~/Games/PitzutzGadol with a menu shortcut.
cd "$(dirname "$0")/.."; D=~/Games/PitzutzGadol
mkdir -p $D ~/.local/share/applications
cp -r Builds/linux/. $D/; rm -rf $D/*_BackUpThisFolder_ButDontShipItWithYourGame
cp Assets/Icons/icon_full.png $D/icon.png
cat > ~/.local/share/applications/pitzutz-gadol.desktop <<DESK
[Desktop Entry]
Type=Application
Name=פיצוץ גדול
Comment=משחק מבוך ורימונים
Exec=$D/PitzutzGadol.x86_64
Path=$D
Icon=$D/icon.png
Terminal=false
Categories=Game;
DESK
chmod +x $D/PitzutzGadol.x86_64 ~/.local/share/applications/pitzutz-gadol.desktop
update-desktop-database ~/.local/share/applications 2>/dev/null
echo installed $D
