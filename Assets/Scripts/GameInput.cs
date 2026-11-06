using UnityEngine;
using UnityEngine.InputSystem;

namespace Pitzutz
{
    /// Keyboard, gamepad and touch, read straight from the devices. The newest direction held wins, and the
    /// one held before it stays as a fallback, so holding two keys turns the first corner that opens.
    public static class GameInput
    {
        static readonly int[] DX = { 1, -1, 0, 0 }, DY = { 0, 0, 1, -1 };
        static readonly int[] since = new int[4];
        static int clock;
        static int lastMajor;       // the stick stays on its axis until the other one clearly wins

        public static Pad Read()
        {
            var held = new bool[4];
            var k = Keyboard.current;
            if (k != null)
            {
                held[0] |= k.rightArrowKey.isPressed || k.dKey.isPressed;
                held[1] |= k.leftArrowKey.isPressed || k.aKey.isPressed;
                held[2] |= k.upArrowKey.isPressed || k.wKey.isPressed;
                held[3] |= k.downArrowKey.isPressed || k.sKey.isPressed;
            }
            var g = Gamepad.current;
            Vector2 stick = Vector2.zero;
            if (g != null)
            {
                var d = g.dpad.ReadValue();
                held[0] |= d.x > .5f; held[1] |= d.x < -.5f; held[2] |= d.y > .5f; held[3] |= d.y < -.5f;
                stick = g.leftStick.ReadValue();
            }
            var touch = TouchUi.Current;
            if (touch != null && touch.Stick.sqrMagnitude > stick.sqrMagnitude) stick = touch.Stick;

            var p = new Pad();
            clock++;
            int first = -1, second = -1;
            for (int i = 0; i < 4; i++)
            {
                if (!held[i]) { since[i] = 0; continue; }
                if (since[i] == 0) since[i] = clock;
                if (first < 0 || since[i] > since[first]) { second = first; first = i; }
                else if (second < 0 || since[i] > since[second]) second = i;
            }
            if (first >= 0) { p.mx = DX[first]; p.my = DY[first]; }
            if (second >= 0) { p.sx = DX[second]; p.sy = DY[second]; }

            if (first < 0 && stick.sqrMagnitude > .08f) Analog(stick, ref p); else if (first < 0) lastMajor = 0;

            if (k != null) p.bomb |= k.spaceKey.wasPressedThisFrame;
            if (g != null) p.bomb |= g.buttonSouth.wasPressedThisFrame || g.buttonEast.wasPressedThisFrame || g.buttonWest.wasPressedThisFrame || g.buttonNorth.wasPressedThisFrame || g.rightTrigger.wasPressedThisFrame;
            if (touch != null) p.bomb |= touch.BombPressed;
            return p;
        }

        static void Analog(Vector2 s, ref Pad p)
        {
            float ax = Mathf.Abs(s.x), ay = Mathf.Abs(s.y);
            bool horizontal = lastMajor == 1 ? ax * 1.3f >= ay : lastMajor == 2 ? ax > ay * 1.3f : ax >= ay;
            lastMajor = horizontal ? 1 : 2;
            if (horizontal)
            {
                p.mx = (int)Mathf.Sign(s.x);
                if (ay > ax * .38f) p.sy = (int)Mathf.Sign(s.y);
            }
            else
            {
                p.my = (int)Mathf.Sign(s.y);
                if (ax > ay * .38f) p.sx = (int)Mathf.Sign(s.x);
            }
        }

        /// True from the moment a gamepad is used until the next touch; the touch buttons hide meanwhile.
        public static bool GamepadActive { get; private set; }
        public static void TouchUsed() => GamepadActive = false;

        public static void Tick()
        {
            var g = Gamepad.current;
            if (g == null) { GamepadActive = false; return; }
            if (g.leftStick.ReadValue().sqrMagnitude > .16f || g.dpad.ReadValue().sqrMagnitude > .16f || g.buttonSouth.isPressed || g.buttonEast.isPressed || g.buttonWest.isPressed
                || g.buttonNorth.isPressed || g.startButton.isPressed || g.rightTrigger.isPressed || g.rightShoulder.isPressed || g.leftShoulder.isPressed) GamepadActive = true;
        }

        static bool systemBack;

        /// The Android Back button, sent by the GameMain activity. Whichever screen reads it first uses it.
        public static void SystemBack() => systemBack = true;
        static bool TakeSystemBack() { bool b = systemBack; systemBack = false; return b; }

        public static bool PausePressed => TakeSystemBack() || Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame
            || Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame
            || TouchUi.Current != null && TouchUi.Current.PausePressed;

        static float nextRepeat;
        static int heldX, heldY;

        /// Menu navigation: one step per press, with auto-repeat while held.
        public static (int x, int y, bool ok, bool back) Menu()
        {
            var k = Keyboard.current; var g = Gamepad.current;
            int x = 0, y = 0; bool ok = false, back = false;
            if (k != null)
            {
                if (k.leftArrowKey.isPressed || k.aKey.isPressed) x -= 1;
                if (k.rightArrowKey.isPressed || k.dKey.isPressed) x += 1;
                if (k.upArrowKey.isPressed || k.wKey.isPressed) y += 1;
                if (k.downArrowKey.isPressed || k.sKey.isPressed) y -= 1;
                ok |= k.spaceKey.wasPressedThisFrame || k.enterKey.wasPressedThisFrame;
                back |= k.escapeKey.wasPressedThisFrame;
            }
            if (g != null)
            {
                Vector2 s = g.leftStick.ReadValue() + g.dpad.ReadValue();
                if (Mathf.Abs(s.x) > .5f) x += (int)Mathf.Sign(s.x);
                if (Mathf.Abs(s.y) > .5f) y += (int)Mathf.Sign(s.y);
                ok |= g.buttonSouth.wasPressedThisFrame || g.startButton.wasPressedThisFrame;
                back |= g.buttonEast.wasPressedThisFrame;
            }
            back |= TakeSystemBack();
            x = Mathf.Clamp(x, -1, 1); y = Mathf.Clamp(y, -1, 1);
            float now = Time.unscaledTime;
            bool changed = x != heldX || y != heldY;
            heldX = x; heldY = y;
            if (changed) nextRepeat = now + .35f;
            else if ((x != 0 || y != 0) && now >= nextRepeat) nextRepeat = now + .12f;
            else { x = 0; y = 0; }
            return (x, y, ok, back);
        }
    }
}
