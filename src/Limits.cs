using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace NudgeClock
{
    // A session whose last turn ended on a usage limit ("You've hit your 5-hour limit · resets 1:40am").
    class LimitHit
    {
        public string Id;           // the transcript entry, so each stop is answered only once
        public DateTime At;         // when the session stopped (local time, MinValue if unknown)
        public DateTime ResetsAt;   // when the limit resets (local time, MinValue if unknown)
        public string Kind;         // "5-hour", "weekly", ...
        public string Text;         // what Claude Code showed

        public bool Known { get { return ResetsAt != DateTime.MinValue; } }
    }

    // Reads each session's transcript (<claude dir>\projects\<project>\<session id>.jsonl) to tell whether its
    // last turn ended on a usage limit, and when that limit resets. After the first look only new bytes are read.
    class Limits
    {
        class Track
        {
            public string Path;
            public long Offset = -1, Length = -1;
            public DateTime Mtime;
            public LimitHit Hit;
            public DateTime FindAgain;
        }

        const int MaxRead = 2 * 1024 * 1024;   // the end of a transcript is all that matters

        static readonly Regex SafeId = new Regex(@"^[A-Za-z0-9_-]{1,100}$");
        static readonly Regex LimitWords = new Regex(@"usage limit|limit reached|hit your .{1,40}limit", RegexOptions.IgnoreCase);
        static readonly Regex HitYour = new Regex(@"hit your (.{1,30}?) limit", RegexOptions.IgnoreCase);
        static readonly Regex UnixStamp = new Regex(@"\|\s*(\d{10,13})\b");
        static readonly Regex ResetsWhen = new Regex(
            @"\bresets?\s+(?:at\s+)?(?:(?<mon>[a-z]{3,9})\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s+(?:at\s+)?)?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>[ap]\.?m\b\.?)?",
            RegexOptions.IgnoreCase);
        static readonly string[] Months = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
        static readonly LimitHit Ordinary = new LimitHit();   // stands for a normal reply

        readonly Dictionary<string, Track> tracks = new Dictionary<string, Track>();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        // The usage limit this session is stopped on right now, or null.
        public LimitHit Check(Session s)
        {
            if (string.IsNullOrEmpty(s.SessionId) || !SafeId.IsMatch(s.SessionId)) return null;
            Track t;
            if (!tracks.TryGetValue(s.SessionId, out t)) { t = new Track(); tracks[s.SessionId] = t; }
            try
            {
                if (t.Path == null || !File.Exists(t.Path))
                {
                    if (DateTime.UtcNow < t.FindAgain) return null;
                    t.Path = Find(s);
                    t.Offset = -1; t.Length = -1; t.Hit = null;
                    if (t.Path == null) { t.FindAgain = DateTime.UtcNow.AddSeconds(15); return null; }
                }
                var fi = new FileInfo(t.Path);
                long len = fi.Length;
                DateTime mt = fi.LastWriteTimeUtc;
                if (len == t.Length && mt == t.Mtime) return t.Hit;
                if (len < t.Offset) { t.Offset = -1; t.Hit = null; }   // the file was rewritten: read it again
                Read(t, len);
                t.Length = len;
                t.Mtime = mt;
            }
            catch { }
            return t.Hit;
        }

        // Drops what it knows about sessions that are gone.
        public void Forget(IEnumerable<Session> live)
        {
            var keep = new HashSet<string>(live.Where(s => s.SessionId != null).Select(s => s.SessionId));
            foreach (string k in tracks.Keys.Where(k => !keep.Contains(k)).ToList()) tracks.Remove(k);
        }

        static string Find(Session s)
        {
            string dir = Path.Combine(Claude.Root(), "projects");
            string name = s.SessionId + ".jsonl";
            try
            {
                // the folder is named after the working directory, with every other character turned into '-'
                if (!string.IsNullOrEmpty(s.Cwd))
                {
                    string p = Path.Combine(dir, Regex.Replace(s.Cwd, "[^A-Za-z0-9]", "-"), name);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            if (!Directory.Exists(dir)) return null;
            foreach (string sub in Directory.GetDirectories(dir))   // long paths get a shortened folder name: look in all of them
            {
                string p = Path.Combine(sub, name);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        void Read(Track t, long len)
        {
            long start = t.Offset < 0 ? 0 : t.Offset;
            bool cut = false;   // starts in the middle of a line
            if (len - start > MaxRead) { start = len - MaxRead; cut = true; }
            if (start >= len) { t.Offset = len; return; }

            var buf = new byte[len - start];
            int got = 0;
            using (var fs = new FileStream(t.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(start, SeekOrigin.Begin);
                int n;
                while (got < buf.Length && (n = fs.Read(buf, got, buf.Length - got)) > 0) got += n;
            }

            int from = 0;
            if (cut)
            {
                int nl = Array.IndexOf(buf, (byte)'\n', 0, got);
                from = nl < 0 ? got : nl + 1;
            }
            int end = from < got ? Array.LastIndexOf(buf, (byte)'\n', got - 1, got - from) : -1;
            if (end < from) { t.Offset = start + from; return; }   // no complete line yet
            t.Offset = start + end + 1;

            // Only the last reply counts: walk back from the end until one turns up.
            string[] lines = Encoding.UTF8.GetString(buf, from, end - from).Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                LimitHit h = Look(lines[i]);
                if (h == null) continue;
                t.Hit = h == Ordinary ? null : h;
                return;
            }
        }

        // null: not a reply of the main conversation · Ordinary: a normal reply · otherwise the usage limit it stopped on
        LimitHit Look(string line)
        {
            if (line.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0) return null;
            Dictionary<string, object> d;
            try { d = json.DeserializeObject(line.Trim()) as Dictionary<string, object>; }
            catch { return null; }
            if (d == null || Str(d, "type") != "assistant") return null;
            object side;
            if (d.TryGetValue("isSidechain", out side) && side is bool && (bool)side) return null;

            Dictionary<string, object> msg = Obj(d, "message");
            string text = FirstText(msg);
            bool synthetic = Str(msg, "model") == "<synthetic>";
            if (Str(d, "apiError") != "usage_limit_reached" && !(synthetic && text != null && LimitWords.IsMatch(text)))
                return Ordinary;

            var hit = new LimitHit { Id = Str(d, "uuid"), Text = text ?? "", At = ParseIso(Str(d, "timestamp")) };
            Dictionary<string, object> info = Obj(Obj(d, "apiErrorParams"), "rate_limit_info");
            Dictionary<string, object> quota = Obj(d, "quotaLimits");
            hit.ResetsAt = ResetTime(info);
            if (!hit.Known) hit.ResetsAt = ResetTime(quota);
            if (!hit.Known) hit.ResetsAt = FromText(hit.Text, hit.At != DateTime.MinValue ? hit.At : DateTime.Now);
            hit.Kind = KindName(Str(info, "rateLimitType") ?? Str(quota, "rateLimitType"), hit.Text);
            if (string.IsNullOrEmpty(hit.Id)) hit.Id = "at-" + hit.At.Ticks;
            return hit;
        }

        static string KindName(string type, string text)
        {
            switch (type)
            {
                case "five_hour": return "5-hour";
                case "seven_day": return "weekly";
                case "seven_day_opus": return "weekly Opus";
                case "seven_day_sonnet": return "weekly Sonnet";
            }
            if (!string.IsNullOrEmpty(type)) return type.Replace('_', ' ');
            Match m = HitYour.Match(text ?? "");
            return m.Success ? m.Groups[1].Value.Trim() : "usage";
        }

        static DateTime ResetTime(Dictionary<string, object> info)
        {
            object v;
            if (info == null || !info.TryGetValue("resetsAt", out v) || v == null || v is bool) return DateTime.MinValue;
            string s = v as string;
            if (s != null)
            {
                long n;
                return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? FromUnix(n) : ParseIso(s);
            }
            try { return FromUnix(Convert.ToInt64(v, CultureInfo.InvariantCulture)); }
            catch { return DateTime.MinValue; }
        }

        // Older or unusual records: "Claude AI usage limit reached|1795243200" or "... resets 1:40am (America/Toronto)".
        // A time without a date is the next one after the stop. The time zone shown is taken to be this PC's.
        public static DateTime FromText(string text, DateTime at)
        {
            if (string.IsNullOrEmpty(text)) return DateTime.MinValue;
            Match m = UnixStamp.Match(text);
            long unix;
            if (m.Success && long.TryParse(m.Groups[1].Value, out unix)) return FromUnix(unix);

            m = ResetsWhen.Match(text);
            if (!m.Success) return DateTime.MinValue;
            int h = int.Parse(m.Groups["h"].Value);
            int min = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value) : 0;
            string ap = m.Groups["ap"].Value.ToLowerInvariant();
            if (ap.Length > 0)
            {
                if (h < 1 || h > 12) return DateTime.MinValue;
                h %= 12;
                if (ap[0] == 'p') h += 12;
            }
            else if (!m.Groups["m"].Success) return DateTime.MinValue;   // a lone number isn't a time
            if (h > 23 || min > 59) return DateTime.MinValue;

            if (m.Groups["mon"].Success)
            {
                int mon = Array.IndexOf(Months, m.Groups["mon"].Value.Substring(0, 3).ToLowerInvariant()) + 1;
                int day = int.Parse(m.Groups["day"].Value);
                if (mon > 0 && day >= 1 && day <= DateTime.DaysInMonth(at.Year, mon))
                {
                    var d = new DateTime(at.Year, mon, day, h, min, 0, DateTimeKind.Local);
                    if (d < at.AddDays(-30)) d = d.AddYears(1);   // "Jan 3" seen in late December
                    return d;
                }
            }
            var r = new DateTime(at.Year, at.Month, at.Day, h, min, 0, DateTimeKind.Local);
            if (r <= at) r = r.AddDays(1);
            return r;
        }

        static DateTime FromUnix(long v)
        {
            if (v <= 0) return DateTime.MinValue;
            if (v > 100000000000L) v /= 1000;   // milliseconds
            try { return When.Epoch.AddSeconds(v).ToLocalTime(); }
            catch { return DateTime.MinValue; }
        }

        static DateTime ParseIso(string s)
        {
            DateTime t;
            if (!string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
                return t.ToLocalTime();
            return DateTime.MinValue;
        }

        static string FirstText(Dictionary<string, object> msg)
        {
            object c;
            if (msg == null || !msg.TryGetValue("content", out c) || c == null) return null;
            string s = c as string;
            if (s != null) return s;
            var list = c as IList;
            if (list == null) return null;
            foreach (object o in list)
            {
                var d = o as Dictionary<string, object>;
                if (d != null && Str(d, "type") == "text") return Str(d, "text");
            }
            return null;
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v as string : null;
        }

        static Dictionary<string, object> Obj(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
        }
    }
}
