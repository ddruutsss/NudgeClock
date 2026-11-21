using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NudgeClock
{
    // Turns what the user types ("25m", "1h30", "at 15:30", "3pm", "1:30:00") into a moment in time.
    static class When
    {
        static readonly Regex AmPm = new Regex(@"^(\d{1,2})(?:[:.h](\d{2}))?\s*(am|pm|a|p)$");
        static readonly Regex Clock = new Regex(@"^(\d{1,2}):(\d{2})$");   // "1h30" stays a countdown
        static readonly Regex HourOnly = new Regex(@"^(\d{1,2})$");
        static readonly Regex Hms = new Regex(@"^(\d+):(\d{1,2}):(\d{1,2})$");
        static readonly Regex Part = new Regex(
            @"\G(\d+(?:\.\d+)?)(hours|hour|hrs|hr|h|minutes|minute|mins|min|m|seconds|second|secs|sec|s)?");

        // isClock: a time of day (repeat = 1 day); otherwise a countdown (repeat = its own length).
        public static bool TryParse(string input, DateTime now, out DateTime target, out TimeSpan repeat,
            out bool isClock, out string error)
        {
            target = now; repeat = TimeSpan.Zero; isClock = false; error = null;
            string s = (input ?? "").Trim().ToLowerInvariant();
            bool at = false;
            if (s.StartsWith("at ")) { at = true; s = s.Substring(3).Trim(); }
            else if (s.StartsWith("@")) { at = true; s = s.Substring(1).Trim(); }
            else if (s.StartsWith("in ")) s = s.Substring(3).Trim();
            if (s.Length == 0) { error = "nothing to read"; return false; }

            Match m = AmPm.Match(s);
            if (m.Success)
            {
                int h = int.Parse(m.Groups[1].Value);
                int min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
                if (h < 1 || h > 12 || min > 59) { error = "that isn't a time of day"; return false; }
                h %= 12;
                if (m.Groups[3].Value.StartsWith("p")) h += 12;
                return SetClock(now, h, min, out target, out repeat, out isClock);
            }
            m = Clock.Match(s);
            if (m.Success)
            {
                int h = int.Parse(m.Groups[1].Value), min = int.Parse(m.Groups[2].Value);
                if (h > 23 || min > 59) { error = "that isn't a time of day"; return false; }
                return SetClock(now, h, min, out target, out repeat, out isClock);
            }
            if (at)
            {
                m = HourOnly.Match(s);
                if (m.Success && int.Parse(m.Groups[1].Value) <= 23)
                    return SetClock(now, int.Parse(m.Groups[1].Value), 0, out target, out repeat, out isClock);
                error = "try  at 15:30  or  at 3pm";
                return false;
            }

            TimeSpan d;
            if (!TryDuration(s, out d, out error)) return false;
            target = now + d;
            repeat = d;
            return true;
        }

        public static bool TryDuration(string input, out TimeSpan span, out string error)
        {
            span = TimeSpan.Zero; error = null;
            string s = (input ?? "").Trim().ToLowerInvariant();
            double secs = 0;

            Match hms = Hms.Match(s);
            if (hms.Success)
            {
                secs = int.Parse(hms.Groups[1].Value) * 3600.0 + int.Parse(hms.Groups[2].Value) * 60.0 + int.Parse(hms.Groups[3].Value);
            }
            else
            {
                string t = s.Replace(" ", "").Replace(",", ".").Replace("and", "");
                int pos = 0, parts = 0;
                char last = ' ';
                while (pos < t.Length)
                {
                    Match m = Part.Match(t, pos);
                    if (!m.Success || m.Length == 0) { error = "didn't understand \"" + input.Trim() + "\" (type help)"; return false; }
                    double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    string u = m.Groups[2].Value;
                    char k;
                    if (u.Length > 0) k = u[0];
                    else if (last == 'h') k = 'm';      // 1h30   -> 1h 30m
                    else if (last == 'm') k = 's';      // 2m30   -> 2m 30s
                    else if (parts == 0) k = 'm';       // 45     -> 45 minutes
                    else { error = "didn't understand \"" + input.Trim() + "\" (type help)"; return false; }
                    secs += v * (k == 'h' ? 3600 : k == 'm' ? 60 : 1);
                    last = k; parts++;
                    pos += m.Length;
                }
            }
            if (secs < 1) { error = "the timer has to be at least 1 second"; return false; }
            if (secs > 7 * 86400) { error = "that's more than a week — keep it under 7 days"; return false; }
            span = TimeSpan.FromSeconds(Math.Round(secs));
            return true;
        }

        static bool SetClock(DateTime now, int h, int min, out DateTime target, out TimeSpan repeat, out bool isClock)
        {
            target = now.Date.AddHours(h).AddMinutes(min);
            if (target <= now) target = target.AddDays(1);
            repeat = TimeSpan.FromDays(1);
            isClock = true;
            return true;
        }

        // 2h 05m · 5m 30s · 45s · 1d 3h
        public static string Span(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            long s = (long)Math.Ceiling(t.TotalSeconds - 0.001);
            long d = s / 86400, h = (s % 86400) / 3600, m = (s % 3600) / 60, sec = s % 60;
            if (d > 0) return h == 0 ? d + "d" : d + "d " + h + "h";
            if (h > 0) return h + "h " + m.ToString("00") + "m";
            if (m > 0) return sec == 0 ? m + "m" : m + "m " + sec.ToString("00") + "s";
            return sec + "s";
        }

        public static string Age(long unixMs)
        {
            if (unixMs <= 0) return "";
            TimeSpan t = DateTime.UtcNow - Epoch.AddMilliseconds(unixMs);
            if (t.TotalMinutes < 1) return "now";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + "m";
            if (t.TotalDays < 1) return (int)t.TotalHours + "h";
            return (int)t.TotalDays + "d";
        }

        public static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long NowMs() { return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds; }

        public static string Stamp(DateTime t, DateTime now)
        {
            return t.Date == now.Date ? t.ToString("HH:mm:ss") : t.ToString("ddd HH:mm:ss");
        }

        // 01:40 · Mon 10:00 (no seconds: for reset times)
        public static string Short(DateTime t, DateTime now)
        {
            return t.Date == now.Date ? t.ToString("HH:mm") : t.ToString("ddd HH:mm");
        }
    }
}
