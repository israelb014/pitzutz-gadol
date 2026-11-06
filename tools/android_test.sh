#!/bin/bash
# Usage: tools/android_test.sh ; plays the built APK on the emulator, in tests and by hand through adb. Never use am force-stop here.
cd "$(dirname "$0")/.."; V=$PWD/verification/android; mkdir -p "$V"; R=$V/android_report.txt; : > "$R"
export PATH=~/work/_tools/android-emu/platform-tools:$PATH
P=com.ibfix.pitzutzgadol; A=$P/.GameMain
if [ "$(adb get-state 2>/dev/null)" != device ]; then
  export ANDROID_SDK_ROOT=~/work/_tools/android-emu ANDROID_AVD_HOME=~/work/_tools/android-emu/avd
  setsid nohup ~/work/_tools/heavy.sh $ANDROID_SDK_ROOT/emulator/emulator -avd s24 -no-window -no-boot-anim -no-snapshot -gpu host > ~/work/_tools/android-emu/emu.log 2>&1 &
  timeout 300 adb wait-for-device || exit 1
  until [ "$(adb shell getprop sys.boot_completed | tr -d '\r')" = 1 ]; do sleep 3; done; sleep 15
fi
bad=0; fail() { echo "FAIL $1" | tee -a "$R"; bad=1; }; ok() { echo "PASS $1" | tee -a "$R"; }
adb install -r Builds/android/PitzutzGadol-android.apk > /dev/null || fail install
log() { adb logcat -d -s Unity:I; }

inapk() { # flag marker seconds
  adb logcat -c; adb shell am start -n $A -e unity "$1" > /dev/null
  for i in $(seq $3); do sleep 1; log | grep -qE " $2 (ALL PASS|[0-9]+ (FAILED|ERRORS))" && break; done
  log | grep -E " $2 " | sed "s/.* $2 //" > "$V/${1#-}.txt"
  grep -q "^ALL PASS" "$V/${1#-}.txt" && ok "$1 inside the APK" || { fail "$1 inside the APK"; grep -vE "^PASS" "$V/${1#-}.txt" | tail -8 | cut -c1-140 | tee -a "$R"; }
  sleep 4
}
inapk -touchtest TOUCH 60
inapk -audiotest AUDIO 150
inapk -uicheck UI 200
inapk -soaktest SOAK 400

# A normal launch, driven through the screen
adb logcat -c; adb shell am start -n $A > /dev/null; sleep 14
adb exec-out screencap -p > "$V/1_title.png"
adb shell dumpsys window | grep -E "mCurrentRotation|mRotation=" | head -1 | tee -a "$R"
adb shell wm size | tee -a "$R"
adb shell input keyevent KEYCODE_BACK; sleep 1
log | grep -q "SCREEN Title" && ok "title shown" || fail "title"
# First menu row on a 2340x1080 screen: canvas 2340 wide, row at centre +260, y 150 above centre
adb shell input tap 1430 390; sleep 3
log | grep -q "SCREEN Map" && ok "tap opens the map" || fail "map"
adb exec-out screencap -p > "$V/2_map.png"
# First stage node: (520, 285) from centre
adb shell input tap 1690 255; sleep 5
log | grep -q "SCREEN Playing" && ok "tap starts stage" || fail "stage"
adb exec-out screencap -p > "$V/3_card.png"; sleep 3
adb exec-out screencap -p > "$V/4_play.png"
log | grep -q "TOUCHUI shown" && ok "touch buttons shown" || fail "touch buttons"
adb shell input swipe 200 800 330 800 900; sleep .5
adb shell input tap 2150 850; sleep 1
adb exec-out screencap -p > "$V/5_bomb.png"
adb logcat -c
adb shell input keyevent KEYCODE_BACK; sleep 2
log | grep -q "SCREEN Paused" && ok "back button opens the pause menu" || fail "back button"
adb exec-out screencap -p > "$V/6_pause.png"
adb shell input keyevent KEYCODE_BACK; sleep 2
adb logcat -c
adb shell input keyevent KEYCODE_HOME; sleep 4
log | grep -q "AUDIO paused" && ok "sound stops in the background" || fail "background sound"
log | grep -q "SCREEN Paused" && ok "game pauses in the background" || fail "background pause"
adb shell am start -n $A > /dev/null; sleep 6
log | grep -q "AUDIO resumed" && ok "sound comes back" || fail "resume"
adb exec-out screencap -p > "$V/7_back.png"
# Bluetooth-style gamepad hides the buttons
adb shell input keyevent KEYCODE_BACK; sleep 2
adb logcat -c
python3 tools/emu_gamepad.py | adb shell uinput - > /dev/null 2>&1 &
sleep 7; adb exec-out screencap -p > "$V/8_gamepad.png"; wait
log | grep -E "TOUCHUI|SCREEN" | tail -5 | sed 's/.*Unity *: //' | tee -a "$R"
log | grep -q "TOUCHUI hidden" && ok "gamepad hides the touch buttons" || fail "gamepad"
[ "$1" = keep ] || { adb shell input keyevent KEYCODE_HOME; adb emu kill > /dev/null; }
[ $bad = 0 ] && echo "ANDROID OK" | tee -a "$R" || echo "ANDROID FAILED" | tee -a "$R"
exit $bad
