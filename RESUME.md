# איך ממשיכים

קרא DECISIONS.md וזיכרון העבודה. הכול ב-~/work/pitzutz-gadol-unity.

- חוקים: Assets/Scripts/Sim. בדיקה מהירה בלי Unity: tools/simtest (dotnet build; ריצה בלי פרמטרים, או one / trace / battle).
- תצוגה ותפריטים: Assets/Scripts (App, Game, Board, Touch, Tests).
- בנייה: tools/run.sh Linux BuildLinux -buildTarget StandaloneLinux64, ואז Windows, ואז tools/build_android.sh. Linux קודם, כי היא מריצה את הבדיקות.
- בניית ניסיון ובדיקות: tools/run.sh Test BuildLinuxTest; tools/gates.sh; tools/shots.sh.
- סיסמת keystore: ~/work/_secrets/pitzutz-gadol-android.env, קובץ המפתח ב-Keystore/ (לא בגיט). אסור לאבד אותם.

מצב: ראה למטה.

## מצב 2026-11-06 11:05
- הכול כתוב. הסימולציה המכווננת נכנסה, ALL STAGES PASS ב-dotnet.
- ריצת אמולטור ראשונה: 4 הבדיקות בתוך ה-APK עברו, האמולטור קרס ב-09:07:24 (eglMakeCurrent, -gpu host) והסקריפט נתקע על adb logcat -c.
- ריצה חוזרת אחת עם מגבלת 30 דקות. אם נכשלת: מדלגים על האמולטור וממשיכים לפרסום (הוראת המשתמש).
- נשאר: בנייה רשמית Linux, Windows, Android; צילומים ל-docs/img; commit; ריפו ציבורי; Release; Pages; בדיקת קישורים; התקנה; זיכרון; הודעה סופית.
- לעצור הכול עד 15:45. הדלקת נרות 16:26.

## 11:32
- הריצה החוזרת נעצרה במגבלת 30 הדקות (exit 124). על APK עם הסימולציה המכווננת עברו touch, audio, ui. האמולטור קרס שוב ב-soak, והשלב המונע ביד (Back, השהיה ברקע, הסתרת כפתורים בגיימפד) לא רץ אף פעם. לפי הוראת המשתמש מדלגים על האמולטור וממשיכים לפרסום. לציין בהודעה הסופית.

## סיום 2026-11-06
- v1.0.0 פורסם. דף: https://israelb014.github.io/pitzutz-gadol/ . מותקן ב-~/Games/PitzutzGadol.
