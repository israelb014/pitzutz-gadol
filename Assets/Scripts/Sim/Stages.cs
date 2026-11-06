using System.Collections.Generic;

namespace Pitzutz
{
    public class StageDef
    {
        public string id, name, hint, music;
        public int world, index, bossKind;
        public string[] map;
        public (EnemyKind k, int n)[] foes = new (EnemyKind, int)[0];
        public float density = .62f;
        public float mirageShare, kegShare;
        public bool wind, dark, hedges;
        public CarLane[] lanes = new CarLane[0];
        public (int x0, int y0, int x1, int y1, float pause)[] tracks = new (int, int, int, int, float)[0];
        public Item[] early = new Item[0];
        public bool Boss => bossKind > 0;
        public string Theme => Stages.Themes[world];
        public string Code => Boss ? "בוס " + (world + 1) : (world + 1) + "-" + (index + 1);
    }

    /// All the stages of the story. Map legend, top row first, always 15 by 13:
    /// # wall   . kept free   space maybe a block   B always a block   S start   C cracked wall (secret)   * secret tile
    /// ^ v &lt; &gt; belts   i ice   ~ ice under a maybe-block   q quicksand   a b c portals (pairs)   U D L R one-way
    /// x crumbling floor   n s e w fire jet (fires up, down, right, left)   o road   k powder keg   r red gate   u blue gate   t switch
    public static class Stages
    {
        public static readonly string[] Themes = { "city", "desert", "factory", "fortress" };
        public static readonly string[] WorldNames = { "עיר", "מדבר", "מפעל", "מבצר" };
        public static readonly string[] Music = { "m_city", "m_desert", "m_factory", "m_fortress" };

        static StageDef S(int w, int i, string name, string hint, string[] map, params (EnemyKind, int)[] foes) =>
            new StageDef { world = w, index = i, id = $"w{w + 1}s{i + 1}", name = name, hint = hint, map = map, foes = foes, music = Music[w] };

        static StageDef B(int w, string name, string hint, string[] map) =>
            new StageDef { world = w, index = 4, id = $"w{w + 1}boss", name = name, hint = hint, map = map, bossKind = w + 1, music = "m_boss", density = .0f };

        const EnemyKind Sn = EnemyKind.Snail, Be = EnemyKind.Beetle, Gh = EnemyKind.Ghost, Fx = EnemyKind.Fox, Fr = EnemyKind.Frog, Tu = EnemyKind.Turtle, Mi = EnemyKind.Mimic;

        public static readonly StageDef[] All = Make();

        static StageDef[] Make()
        {
            var l = new List<StageDef>();

            // ---- City ----
            var s = S(0, 0, "הרחוב הראשון", "שים רימון, ברח לפינה, וחפש את הפתח מתחת לארגזים", new[]
            {
                "###############",
                "#S.           #",
                "#.# # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#             #",
                "# # # # # C # #",
                "#             #",
                "# # # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#             #",
                "###############",
            }, (Sn, 3));
            s.early = new[] { Item.Bomb, Item.Flame }; s.density = .55f;
            l.Add(s);

            s = S(0, 1, "שעת עומס", "מכוניות בכביש! חצה רק כשהדרך פנויה", new[]
            {
                "###############",
                "#S.           #",
                "#.# # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#ooooooooooooo#",
                "# # # # # # # #",
                "#             #",
                "# # #*# # # # #",
                "#ooooooooooooo#",
                "# # # # # # # #",
                "#             #",
                "###############",
            }, (Sn, 2), (Be, 3));
            s.early = new[] { Item.Speed, Item.Bomb };
            s.lanes = new[] { new CarLane { row = 7, dir = 1, speed = 6, period = 3.2f, phase = 1 }, new CarLane { row = 3, dir = -1, speed = 7, period = 2.7f, phase = 0 } };
            l.Add(s);

            // Three rooms; a links left and middle, b middle and right, c right and left
            s = S(0, 2, "הרכבת התחתית", "שלושה חדרים, ומחברים אותם רק השערים הזוהרים", new[]
            {
                "###############",
                "#S. #     #   #",
                "#.# # # # # # #",
                "# .a#.b.  # .c#",
                "# # # # # # # #",
                "#   #     #   #",
                "# # # # C # # #",
                "#   #  .  #   #",
                "# # # # # # # #",
                "#.c #  .a.#.b #",
                "# # # # # # # #",
                "#   #     #   #",
                "###############",
            }, (Be, 3), (Sn, 2));
            s.early = new[] { Item.Kick, Item.Flame };
            l.Add(s);

            s = S(0, 3, "גן העיר", "השיחים צומחים בחזרה. אל תתמהמה", new[]
            {
                "###############",
                "#S.           #",
                "#.# # # # # # #",
                "#        *    #",
                "# # # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#             #",
                "###############",
            }, (Be, 3), (Fx, 1), (Gh, 1));
            s.early = new[] { Item.Shield, Item.Bomb }; s.hedges = true; s.density = .6f;
            l.Add(s);

            l.Add(B(0, "מלך האשפה", "הוא נוגח בקו ישר. זוז מהשורה המסומנת, ופגע בו כשהוא מסוחרר", new[]
            {
                "###############",
                "#S...........*#",
                "#.#.........#.#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.#.........#.#",
                "#.............#",
                "###############",
            }));

            // ---- Desert ----
            l.Add(S(1, 0, "חולות טובעניים", "בחול הטובעני הולכים לאט. תכנן את הבריחה מראש", new[]
            {
                "###############",
                "#S.     q     #",
                "#.# # #q#q# # #",
                "#    qqqqq    #",
                "# # #q#q#q# # #",
                "#  qqqq qqqq  #",
                "# #q# # # #q# #",
                "#  qqqq qqqq  #",
                "# # #q#q#q# # #",
                "#    qqqqq    #",
                "# # #q#q#qC # #",
                "#       q     #",
                "###############",
            }, (Be, 3), (Fr, 2)));

            s = S(1, 1, "סופת רוח", "כשהחץ מופיע הרוח תדחוף אותך ואת הרימונים", new[]
            {
                "###############",
                "#S.           #",
                "#.# # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#    *        #",
                "# # # # # # # #",
                "#             #",
                "###############",
            }, (Be, 3), (Fr, 2), (Gh, 1));
            s.wind = true; s.density = .5f;
            l.Add(s);

            s = S(1, 2, "תעתועים", "חלק מהקירות הם רק מראה. נסה לעבור דרכם", new[]
            {
                "###############",
                "#S.           #",
                "#.# ### # ### #",
                "#             #",
                "# # # # # # # #",
                "#    ## ##    #",
                "# # # C # # # #",
                "#    ## ##    #",
                "# # # # # # # #",
                "#             #",
                "# ### # ### # #",
                "#             #",
                "###############",
            }, (Gh, 3), (Fr, 2), (Fx, 1));
            s.mirageShare = .4f; s.density = .75f;
            l.Add(s);

            s = S(1, 3, "לילה במדבר", "חשוך. רק הפנס, הרימונים והלהבות מאירים", new[]
            {
                "###############",
                "#S.           #",
                "#.# # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#          *  #",
                "###############",
            }, (Gh, 3), (Fx, 2), (Be, 2));
            s.dark = true; s.density = .55f;
            l.Add(s);

            l.Add(B(1, "עקרב החולות", "הוא חופר ומתפרץ מתחתיך. התרחק מהריבוע המסומן", new[]
            {
                "###############",
                "#S............#",
                "#.#.........#.#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.#.........#.#",
                "#............*#",
                "###############",
            }));

            // ---- Factory ----
            l.Add(S(2, 0, "פס ייצור", "המסועים סוחבים אותך ואת הרימונים", new[]
            {
                "###############",
                "#S.           #",
                "#.# # # # # # #",
                "#  >>>>>>>>v  #",
                "# #^# # # #v# #",
                "#  ^       v  #",
                "# #^# C # #v# #",
                "#  ^       v  #",
                "# #^# # # #v# #",
                "#  ^<<<<<<<<  #",
                "# # # # # # # #",
                "#             #",
                "###############",
            }, (Be, 3), (Tu, 2), (Mi, 1)));

            s = S(2, 1, "קירות זזים", "קירות הפלדה נעים הלוך ושוב. אל תיתפס ביניהם לקיר", new[]
            {
                "###############",
                "#S.           #",
                "#.# # # # # # #",
                "#  .........  #",
                "# # # # # # # #",
                "#             #",
                "# # # # # C # #",
                "#  .........  #",
                "# # # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#  .........  #",
                "###############",
            }, (Tu, 2), (Fx, 2), (Fr, 1), (Mi, 1));
            s.tracks = new[] { (3, 9, 11, 9, 1.1f), (11, 5, 3, 5, .9f), (3, 1, 11, 1, 1.0f) };
            l.Add(s);

            s = S(2, 2, "מחסן הקירור", "על הקרח אי אפשר לעצור. רק קיר יעצור אותך", new[]
            {
                "###############",
                "#S.~~~~~~~~~~ #",
                "#.#~#~#~#~#~# #",
                "#~~~~~~~~~~~~~#",
                "#~#~#~#~#~#~#~#",
                "#~~~~~~~~~~~~~#",
                "#~#~#~#~#~C~#~#",
                "#~~~~~~~~~~~~~#",
                "#~#~#~#~#~#~#~#",
                "#~~~~~~~~~~~~~#",
                "#~#~#~#~#~#~#~#",
                "#~~~~~~~~~~~~~#",
                "###############",
            }, (Gh, 1), (Tu, 2), (Fx, 2));
            s.density = .5f;
            l.Add(s);

            // Three halls in a row. Red and blue are never open together, so each hall needs its own switch
            s = S(2, 3, "שערי צבע", "להבה על המתג מחליפה את השערים האדומים והכחולים", new[]
            {
                "###############",
                "#S.           #",
                "#.# # # # # # #",
                "#     .t.     #",
                "###########r###",
                "#             #",
                "# # # # C # # #",
                "#     .t.     #",
                "###u###########",
                "#             #",
                "# # # # # # # #",
                "#     .t.     #",
                "###############",
            }, (Tu, 2), (Fx, 1), (Mi, 2), (Gh, 1));
            s.density = .5f;
            l.Add(s);

            l.Add(B(2, "המכבש", "הוא קופץ ונוחת עליך. גם גלגלי שיניים יתגלגלו בשורות המסומנות", new[]
            {
                "###############",
                "#S............#",
                "#.#.........#.#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.#.........#.#",
                "#*............#",
                "###############",
            }));

            // ---- Fortress ----
            s = S(3, 0, "דלתות חד כיווניות", "החצים נותנים לעבור רק לכיוון אחד. בחר דרך ואל תתחרט", new[]
            {
                "###############",
                "#S.           #",
                "#.# # # # # # #",
                "#     ...   ..#",
                "##D####U##D##U#",
                "#....    ...  #",
                "# #.# # # C # #",
                "#  ...     ...#",
                "#D##U##D####U##",
                "#..   ...     #",
                "#.# # # # # # #",
                "#             #",
                "###############",
            }, (Fr, 2), (Tu, 2), (Fx, 1), (Gh, 1));
            s.density = .48f;
            l.Add(s);

            l.Add(S(3, 1, "רצפה מתפוררת", "כל צעד סודק את האבן, ובסוף היא נופלת ללבה", new[]
            {
                "###############",
                "#S.xxxxxxxxxxx#",
                "#.#x#x#x#x#x#x#",
                "#xxxx     xxxx#",
                "#x#x# # # #x#x#",
                "#xx         xx#",
                "#x#x# # C #x#x#",
                "#xx         xx#",
                "#x#x# # # #x#x#",
                "#xxxx     xxxx#",
                "#x#x#x#x#x#x#x#",
                "#xxxxxxxxxxxxx#",
                "###############",
            }, (Fr, 2), (Fx, 3), (Tu, 2)));

            s = S(3, 2, "סילוני אש", "החרירים זורחים לפני שהם יורים. עבור בין היריות", new[]
            {
                "###############",
                "#S.  e        #",
                "#.# # # # # # #",
                "#      w      #",
                "# #s# # # # # #",
                "#    e    w   #",
                "# # # #n# # # #",
                "#   e   w     #",
                "# # # # # #n# #",
                "#  e      *   #",
                "# # #n# # # # #",
                "#         w   #",
                "###############",
            }, (Tu, 2), (Fx, 2), (Gh, 1), (Mi, 1));
            s.density = .5f;
            l.Add(s);

            s = S(3, 3, "מחסן אבק השריפה", "החביות מתפוצצות בשרשרת, וגם אתה יכול להיתפס", new[]
            {
                "###############",
                "#S.           #",
                "#.# # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#             #",
                "# # # # # # # #",
                "#      *      #",
                "# # # # # # # #",
                "#             #",
                "###############",
            }, (Tu, 3), (Fx, 3), (Fr, 2), (Mi, 1), (Gh, 1));
            s.kegShare = .3f; s.density = .72f;
            l.Add(s);

            l.Add(B(3, "לורד הגחלים", "אש בשורה ובטור, גחלים מהשמיים, והוא נעלם. קרא את הרצפה", new[]
            {
                "###############",
                "#S............#",
                "#.#.........#.#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.............#",
                "#.#.........#.#",
                "#............*#",
                "###############",
            }));
            return l.ToArray();
        }

        public static int IndexOf(int world, int index) => world * 5 + index;
    }
}
