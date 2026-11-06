using System.Collections.Generic;
using System.Text;

namespace Pitzutz
{
    /// uGUI Text has no bidi: turns logical Hebrew into visual order, keeping digit and Latin runs readable.
    public static class Rtl
    {
        static bool Strong(char c) => c < 128 && (char.IsLetterOrDigit(c) || c == '%');
        static bool Weak(char c) => c == ' ' || c == '.' || c == ',' || c == '-' || c == '(' || c == ':' || c == '/';

        static char Mirror(char c) => c == '(' ? ')' : c == ')' ? '(' : c;

        /// Every string this class has produced, kept during the screen check to catch text that skipped it.
        public static HashSet<string> Seen;

        public static string V(string s)
        {
            var lines = s.Split('\n');
            for (int n = 0; n < lines.Length; n++) lines[n] = Line(lines[n]);
            string done = string.Join("\n", lines);
            Seen?.Add(done);
            return done;
        }

        /// Splits into left-to-right runs and single right-to-left characters, then reverses their order.
        static string Line(string s)
        {
            var parts = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                if (!Strong(s[i]) && !(s[i] == '(' && i + 1 < s.Length && Strong(s[i + 1])))
                {
                    parts.Add(Mirror(s[i]).ToString());
                    i++;
                    continue;
                }
                int end = i;   // last character of the run
                for (int j = i; j < s.Length; j++)
                {
                    if (Strong(s[j])) end = j;
                    else if (!Weak(s[j])) break;
                }
                if (end + 1 < s.Length && s[end + 1] == ')' && s.IndexOf('(', i, end - i + 1) >= 0) end++;
                parts.Add(s.Substring(i, end - i + 1));
                i = end + 1;
            }
            parts.Reverse();
            return new StringBuilder().AppendJoin("", parts).ToString();
        }
    }
}
