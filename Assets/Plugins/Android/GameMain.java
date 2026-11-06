package com.ibfix.pitzutzgadol;

import android.content.Context;
import android.media.AudioManager;
import android.os.VibrationEffect;
import android.os.Vibrator;
import android.view.KeyEvent;
import com.unity3d.player.UnityPlayer;
import com.unity3d.player.UnityPlayerGameActivity;

/** Sends the system Back button to the game instead of closing the app. */
public class GameMain extends UnityPlayerGameActivity {
    private static Vibrator vibrator;

    @Override
    protected void onCreate(android.os.Bundle saved) {
        super.onCreate(saved);
        vibrator = (Vibrator) getSystemService(Context.VIBRATOR_SERVICE);
        // The volume keys change the media volume at all times, also on a menu with nothing playing
        setVolumeControlStream(AudioManager.STREAM_MUSIC);
    }

    /** Called from the game. strength is 1 to 255. */
    public static void buzz(int ms, int strength) {
        if (vibrator == null || !vibrator.hasVibrator()) return;
        vibrator.vibrate(VibrationEffect.createOneShot(ms, Math.max(1, Math.min(255, strength))));
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        // Caught here because GameActivity's text input swallows the first Back after launch
        if (event.getKeyCode() == KeyEvent.KEYCODE_BACK) {
            if (event.getAction() == KeyEvent.ACTION_UP && !event.isCanceled()) sendBack();
            return true;
        }
        return super.dispatchKeyEvent(event);
    }

    @Override
    public void onBackPressed() {
        sendBack();
    }

    private void sendBack() {
        UnityPlayer.UnitySendMessage("App", "OnSystemBack", "");
    }
}
