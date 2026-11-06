#!/usr/bin/env python3
"""Prints uinput commands for the Android `uinput -` tool: a virtual gamepad that presses, moves right, and lets go.
Usage: tools/emu_gamepad.py | adb shell uinput -"""
import json, sys, time
EV_SYN, EV_KEY, EV_ABS = "EV_SYN", "EV_KEY", "EV_ABS"
A, B, X, Y, START = "BTN_SOUTH", "BTN_EAST", "BTN_WEST", "BTN_NORTH", "BTN_START"
ABS_X, ABS_Y = "ABS_X", "ABS_Y"
reg = {"id": 1, "command": "register", "name": "Test Gamepad", "vid": 0x045e, "pid": 0x028e, "bus": "bluetooth",
       "configuration": [{"type": "UI_SET_EVBIT", "data": [EV_KEY, EV_ABS]},
                         {"type": "UI_SET_KEYBIT", "data": [A, B, X, Y, START]},
                         {"type": "UI_SET_ABSBIT", "data": [ABS_X, ABS_Y]}],
       "abs_info": [{"code": ABS_X, "info": {"value": 0, "minimum": -32768, "maximum": 32767, "fuzz": 0, "flat": 0, "resolution": 0}},
                    {"code": ABS_Y, "info": {"value": 0, "minimum": -32768, "maximum": 32767, "fuzz": 0, "flat": 0, "resolution": 0}}]}
def out(o): print(json.dumps(o)); sys.stdout.flush()
def inject(*ev): out({"id": 1, "command": "inject", "events": [x for e in ev for x in e] + [EV_SYN, "SYN_REPORT", 0]})
def wait(ms): out({"id": 1, "command": "delay", "duration": ms})
out(reg); wait(1500)
inject((EV_ABS, ABS_X, 32000)); wait(1500)          # stick right
inject((EV_KEY, A, 1)); wait(400); inject((EV_KEY, A, 0)); wait(800)
inject((EV_ABS, ABS_X, 0)); wait(1000)
inject((EV_KEY, START, 1)); wait(200); inject((EV_KEY, START, 0)); wait(1000)
