using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace NudgeClock
{
    class Settings
    {
        public string Message = "continue";
        public bool Loop;
        public bool SkipBusy;
        public bool Auto;                // continue sessions by themselves when their usage limit resets
        public int AutoMarginSec = 60;   // how long after the reset the auto timer fires
        public bool NoSave;   // set by --frame so test runs never touch the .ini
        string path;

        public static Settings Load()
        {
            var s = new Settings();
            s.path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NudgeClock.ini");
            try
            {
                if (File.Exists(s.path))
                    foreach (string raw in File.ReadAllLines(s.path))
                    {
                        int eq = raw.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = raw.Substring(0, eq).Trim().ToLowerInvariant(), v = raw.Substring(eq + 1).Trim();
                        int n;
                        if (k == "message" && v.Length > 0) s.Message = v;
                        else if (k == "loop") s.Loop = v == "1" || v == "true";
                        else if (k == "skipbusy") s.SkipBusy = v == "1" || v == "true";
                        else if (k == "auto") s.Auto = v == "1" || v == "true";
                        else if (k == "automargin" && int.TryParse(v, out n) && n >= 0 && n <= 3600) s.AutoMarginSec = n;
                    }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            if (NoSave) return;
            try
            {
                File.WriteAllLines(path, new[] {
                    "message=" + Message.Replace("\r", " ").Replace("\n", " "),
                    "loop=" + (Loop ? "1" : "0"),
                    "skipbusy=" + (SkipBusy ? "1" : "0"),
                    "auto=" + (Auto ? "1" : "0"),
                    "automargin=" + AutoMarginSec });
            }
            catch { }
        }
    }

    // A countdown ("25m") or a time of day ("at 15:30").
    class Countdown
    {
        public DateTime Target, ArmedAt;
        public TimeSpan Repeat;
        public bool IsClock;

        // loop: the next round once this one has fired (a time of day comes back the next day)
        public void Rearm(DateTime now)
        {
            if (Repeat < TimeSpan.FromSeconds(1)) Repeat = TimeSpan.FromSeconds(1);
            DateTime next = Target + Repeat;
            while (next <= now) next += Repeat;
            ArmedAt = Target;
            Target = next;
        }
    }

    // What one session does besides following the main timer.
    class Plan
    {
        public Countdown Own;   // its own timer: goes before auto and the main timer
        public bool? Auto;      // null: follows the global auto switch
    }

    // A session stopped on a usage limit, waiting for it to reset.
    class AutoWait
    {
        public LimitHit Hit;
        public DateTime Fire, Since;
    }

    class App
    {
        class LogEntry { public DateTime At; public string Text; public Rgb Color; }
        class SendResult { public Session S; public string Error; public DateTime At; }
        class Mark { public bool Ok; public DateTime At; public string Error; }
        class Pending { public string Key, Name; public long SentMs; public DateTime Until; }

        // What the big clock counts down to.
        class Focus
        {
            public Session S;          // the session it's for (null: the main timer, nothing picked)
            public Countdown Timer;    // the main timer or the session's own
            public AutoWait Wait;      // set for an auto timer
            public bool Main;
            public DateTime Target, From;
        }

        static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);     // the limit already reset: wait this long, so it can still be stopped
        static readonly TimeSpan RetryGap = TimeSpan.FromMinutes(5);  // auto sends to the same session at most this often
        static readonly TimeSpan TooOld = TimeSpan.FromHours(12);     // a limit that reset longer ago than this is left alone
        static readonly Regex NumberFirst = new Regex(@"^#?(\d{1,3})\s+(\S.*)$");

        readonly Settings cfg;
        readonly Screen screen = new Screen();
        readonly object gate = new object();
        readonly Limits limits = new Limits();

        List<Session> sessions = new List<Session>();
        string scanError;
        DateTime lastScan = DateTime.MinValue;
        bool firstScan = true;
        string selKey;   // the session picked with Tab (null: all of them)
        readonly Dictionary<string, DateTime> seenAt = new Dictionary<string, DateTime>();
        readonly HashSet<string> off = new HashSet<string>();
        readonly Dictionary<string, Plan> plans = new Dictionary<string, Plan>();
        readonly Dictionary<string, LimitHit> hits = new Dictionary<string, LimitHit>();     // sessions stopped on a usage limit
        readonly Dictionary<string, AutoWait> waits = new Dictionary<string, AutoWait>();    // auto timers
        readonly HashSet<string> answered = new HashSet<string>();   // limit stops already sent to, or dismissed with stop
        readonly Dictionary<string, DateTime> lastSent = new Dictionary<string, DateTime>();

        Countdown main;   // the main timer: for every session that has no timer of its own
        DateTime firedAt = DateTime.MinValue;
        string firedSummary = "";
        int sending;
        bool awake;

        readonly List<LogEntry> log = new List<LogEntry>();
        readonly List<SendResult> inbox = new List<SendResult>();
        readonly Dictionary<string, Mark> marks = new Dictionary<string, Mark>();
        readonly List<Pending> pending = new List<Pending>();

        string input = "";
        readonly List<string> history = new List<string>();
        int histPos = -1;
        bool quit, showHelp;

        public bool DryRun;   // --frame: never touches a real session
        public HashSet<string> Only;   // --only: restrict to these pids / names / session ids

        public App(Settings cfg) { this.cfg = cfg; }

        // ───────────────────────────── main loop ─────────────────────────────

        public int Run(string initial)
        {
            Console.TreatControlCAsInput = true;
            Vt.Enable();
            try { Console.Title = "NudgeClock"; } catch { }
            Console.Out.Write(Vt.ESC + "[?1049h" + Vt.ESC + "[?25l");
            try
            {
                Rescan();
                if (initial.Length > 0) Execute(initial);
                DateTime lastDraw = DateTime.MinValue;
                while (!quit)
                {
                    bool dirty = false;
                    while (Console.KeyAvailable) { OnKey(Console.ReadKey(true)); dirty = true; }
                    if ((DateTime.Now - lastScan).TotalMilliseconds >= 1500) { Rescan(); dirty = true; }
                    if (DrainResults()) dirty = true;
                    if (Tick()) dirty = true;
                    StayAwake();
                    if (dirty || (DateTime.Now - lastDraw).TotalMilliseconds >= 250)
                    {
                        Draw();
                        lastDraw = DateTime.Now;
                    }
                    Thread.Sleep(30);
                }
            }
            finally
            {
                Vt.KeepAwake(false);
                Console.Out.Write(Vt.ESC + "[0m" + Vt.ESC + "[?25h" + Vt.ESC + "[?1049l");
            }
            return 0;
        }

        // --frame: lets time pass, with rescans, timers and deliveries running as they would on screen
        public void Advance(double seconds)
        {
            DateTime until = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < until)
            {
                if ((DateTime.Now - lastScan).TotalMilliseconds >= 1500) Rescan();
                DrainResults();
                Tick();
                Thread.Sleep(50);
            }
            DrainResults();
        }

        // --frame: a key press
        public void Press(ConsoleKey key, bool shift)
        {
            OnKey(new ConsoleKeyInfo('\0', key, shift, false, false));
        }

        void Draw()
        {
            int w, h;
            try { w = Console.WindowWidth - 1; h = Console.WindowHeight; }
            catch { return; }
            if (w < 10 || h < 3) return;
            try { screen.Present(Build(w, h), w, h); }
            catch (IOException) { screen.Invalidate(); }
        }

        // Windows stays awake while any timer is armed (the screen can still turn off).
        void StayAwake()
        {
            bool want = TimerCount() > 0;
            if (want == awake) return;
            awake = want;
            Vt.KeepAwake(want);
        }

        bool Tick()
        {
            DateTime now = DateTime.Now;
            bool fired = false;

            if (main != null && now >= main.Target)
            {
                if (cfg.Loop) main.Rearm(now); else main = null;
                firedAt = now;
                fired = true;
                Deliver(MainTargets(), "time's up · ", "time's up, but ");
            }

            foreach (Session s in sessions)
            {
                Plan p;
                if (!plans.TryGetValue(s.Key, out p) || p.Own == null || now < p.Own.Target) continue;
                if (cfg.Loop) p.Own.Rearm(now); else p.Own = null;
                firedAt = now;
                fired = true;
                string why = Skipped(s);
                if (why != null) { Log(s.Name + "'s timer is up, but it's " + why, C.Warn); continue; }
                Deliver(new List<Session> { s }, s.Name + "'s timer is up · ", null);
            }

            foreach (KeyValuePair<string, AutoWait> kv in waits.ToList())
            {
                if (now < kv.Value.Fire) continue;
                waits.Remove(kv.Key);
                answered.Add(kv.Value.Hit.Id);
                Session s = sessions.FirstOrDefault(x => x.Key == kv.Key);
                if (s == null) continue;
                firedAt = now;
                fired = true;
                Deliver(new List<Session> { s }, "auto · " + s.Name + "'s " + kv.Value.Hit.Kind + " limit is over · ", null);
            }
            return fired;
        }

        void Rescan()
        {
            string err;
            DateTime now = DateTime.Now;
            List<Session> all = Claude.Scan(out err);
            List<Session> fresh = all.Where(s => s.Alive && (Only == null || Program.Matches(Only, s))).ToList();
            // a record caught while it's being rewritten can't be read: its session stays for a few seconds
            var read = new HashSet<string>(all.Select(s => s.Key));
            foreach (Session old in sessions)
            {
                DateTime seen;
                if (!read.Contains(old.Key) && seenAt.TryGetValue(old.Key, out seen) && (now - seen).TotalSeconds < 8
                    && File.Exists(Path.Combine(Claude.SessionsDir(), old.Pid + ".json")))
                    fresh.Add(old);
            }
            fresh.Sort((a, b) => a.StartedAt.CompareTo(b.StartedAt));
            foreach (Session s in fresh) if (read.Contains(s.Key)) seenAt[s.Key] = now;
            scanError = err;
            lastScan = now;
            bool first = firstScan;

            if (!first)
            {
                var oldKeys = new HashSet<string>(sessions.Select(s => s.Key));
                var newKeys = new HashSet<string>(fresh.Select(s => s.Key));
                foreach (Session s in fresh)
                    if (!oldKeys.Contains(s.Key)) Log("+ " + s.Name + " opened (" + s.Source + ")", C.Muted);
                foreach (Session s in sessions)
                    if (!newKeys.Contains(s.Key)) Log("- " + s.Name + " closed", C.Muted);
            }
            else
            {
                Log("ready · found " + fresh.Count + " Claude session" + (fresh.Count == 1 ? "" : "s")
                    + (cfg.Auto ? " · auto on" : "") + " · type help for commands", C.Text);
                if (err != null) Log(err, C.Warn);
            }
            firstScan = false;
            sessions = fresh;

            // forget what belonged to sessions that are gone
            var live = new HashSet<string>(sessions.Select(s => s.Key));
            Prune(plans, live);
            Prune(waits, live);
            Prune(marks, live);
            Prune(lastSent, live);
            Prune(seenAt, live);
            off.RemoveWhere(k => !live.Contains(k));
            if (selKey != null && !live.Contains(selKey)) selKey = null;

            // which ones are stopped on a usage limit
            hits.Clear();
            foreach (Session s in sessions)
            {
                LimitHit h = limits.Check(s);
                if (h != null) hits[s.Key] = h;
            }
            limits.Forget(sessions);
            UpdateAuto();
            if (first && cfg.Auto) ExplainLimits(null);

            // Did the sessions we just nudged actually start working?
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Pending p = pending[i];
                Session s = sessions.FirstOrDefault(x => x.Key == p.Key);
                if (s == null) { pending.RemoveAt(i); continue; }
                if (s.Busy && s.StatusUpdatedAt >= p.SentMs - 1000)
                {
                    Log("► " + s.Name + " is working again", C.Green);
                    pending.RemoveAt(i);
                }
                else if (DateTime.Now > p.Until)
                {
                    Log("… " + s.Name + " hasn't picked it up yet (status: " + s.Status + ")", C.Warn);
                    pending.RemoveAt(i);
                }
            }
        }

        static void Prune<T>(Dictionary<string, T> d, HashSet<string> live)
        {
            foreach (string k in d.Keys.Where(k => !live.Contains(k)).ToList()) d.Remove(k);
        }

        // Auto: each session stopped on a usage limit gets a timer for a bit after that limit resets.
        int UpdateAuto()
        {
            DateTime now = DateTime.Now;
            int added = 0;
            foreach (Session s in sessions)
            {
                LimitHit h;
                hits.TryGetValue(s.Key, out h);
                AutoWait w;
                waits.TryGetValue(s.Key, out w);
                bool want = h != null && h.Known && AutoFor(s) && OwnOf(s) == null && s.CanSend && !off.Contains(s.Key)
                    && !answered.Contains(h.Id) && !WorkingAgain(s, h) && h.ResetsAt >= now - TooOld;
                if (w != null && (!want || w.Hit.Id != h.Id))
                {
                    waits.Remove(s.Key);
                    if (h == null || WorkingAgain(s, h)) Log("► " + s.Name + " is going again · its auto timer isn't needed anymore", C.Green);
                    w = null;
                }
                if (!want || w != null) continue;

                DateTime fire = h.ResetsAt.AddSeconds(cfg.AutoMarginSec);
                if (fire < now + Grace) fire = now + Grace;
                DateTime last;
                bool retry = lastSent.TryGetValue(s.Key, out last) && fire < last + RetryGap;
                if (retry) fire = last + RetryGap;
                waits[s.Key] = new AutoWait { Hit = h, Fire = fire, Since = now };
                added++;
                if (retry)
                    Log("auto · " + s.Name + " is still on its " + h.Kind + " limit · tries again at " + When.Stamp(fire, now)
                        + " (in " + When.Span(fire - now) + ")", C.Amber);
                else
                    Log("auto · " + s.Name + " hit its " + h.Kind + " limit · "
                        + (h.ResetsAt > now ? "resets " + When.Short(h.ResetsAt, now) : "it has already reset")
                        + " · continues at " + When.Stamp(fire, now) + " (in " + When.Span(fire - now) + ")", C.Amber);
            }
            return added;
        }

        // A session that turned busy after it stopped was picked up by someone (or something) else.
        static bool WorkingAgain(Session s, LimitHit h)
        {
            return h != null && s.Busy && h.At != DateTime.MinValue && s.StatusUpdatedAt > ToUnixMs(h.At) + 1000;
        }

        // Why a timer can't send to this session right now (null: it can).
        string Skipped(Session s)
        {
            if (!s.CanSend) return "unreachable (no inbox key)";
            if (off.Contains(s.Key)) return "left out";
            if (cfg.SkipBusy && s.Busy) return "busy (busy sessions are skipped)";
            return null;
        }

        // Everyone the main timer is for: sessions with no timer of their own that aren't waiting on a usage limit.
        List<Session> MainTargets()
        {
            return sessions.Where(s => Skipped(s) == null && OwnOf(s) == null && !waits.ContainsKey(s.Key)).ToList();
        }

        List<Session> Everyone()
        {
            return sessions.Where(s => Skipped(s) == null).ToList();
        }

        Countdown OwnOf(Session s)
        {
            Plan p;
            return plans.TryGetValue(s.Key, out p) ? p.Own : null;
        }

        Plan PlanFor(Session s)
        {
            Plan p;
            if (!plans.TryGetValue(s.Key, out p)) { p = new Plan(); plans[s.Key] = p; }
            return p;
        }

        bool AutoFor(Session s)
        {
            Plan p;
            return plans.TryGetValue(s.Key, out p) && p.Auto.HasValue ? p.Auto.Value : cfg.Auto;
        }

        Session Selected()
        {
            return selKey == null ? null : sessions.FirstOrDefault(s => s.Key == selKey);
        }

        int TimerCount()
        {
            return (main != null ? 1 : 0) + sessions.Count(s => OwnOf(s) != null) + waits.Count;
        }

        // Sends the message to these sessions in the background. `lead` starts the log line, `leadNone` the "nobody" one.
        void Deliver(List<Session> targets, string lead, string leadNone)
        {
            if (targets.Count == 0)
            {
                firedSummary = "no sessions to send to";
                Log((leadNone ?? "") + "there are no sessions to send to", C.Warn);
                return;
            }
            DateTime now = DateTime.Now;
            foreach (Session s in targets)
            {
                lastSent[s.Key] = now;
                LimitHit h;
                if (hits.TryGetValue(s.Key, out h)) answered.Add(h.Id);   // this answers the limit stop it's on
                waits.Remove(s.Key);
            }
            string who = targets.Count == 1 ? targets[0].Name : targets.Count + " sessions";
            firedSummary = "sent \"" + Clip(cfg.Message, 30) + "\" to " + who;
            Log(lead + "sending \"" + Clip(cfg.Message, 40) + "\" to " + who, C.Amber);
            if (DryRun)
            {
                foreach (Session s in targets) Log("  (dry run) would send to " + s.Name, C.Muted);
                return;
            }
            string text = cfg.Message;
            Interlocked.Increment(ref sending);
            var t = new Thread(() =>
            {
                try
                {
                    foreach (Session s in targets)
                    {
                        string e = Claude.Send(s, text);
                        lock (gate) inbox.Add(new SendResult { S = s, Error = e, At = DateTime.Now });
                    }
                }
                finally { Interlocked.Decrement(ref sending); }
            });
            t.IsBackground = true;
            t.Start();
        }

        bool DrainResults()
        {
            List<SendResult> got;
            lock (gate)
            {
                if (inbox.Count == 0) return false;
                got = new List<SendResult>(inbox);
                inbox.Clear();
            }
            foreach (SendResult r in got)
            {
                string k = r.S.Key;
                if (r.Error == null)
                {
                    Log("✓ " + r.S.Name + " got it" + (r.S.Busy ? " (it was busy, so it lands in its current turn)" : ""), C.Green);
                    marks[k] = new Mark { Ok = true, At = r.At };
                    if (!r.S.Busy)
                        pending.Add(new Pending { Key = k, Name = r.S.Name, SentMs = ToUnixMs(r.At), Until = r.At.AddSeconds(45) });
                }
                else
                {
                    Log("× " + r.S.Name + ": " + r.Error, C.Red);
                    marks[k] = new Mark { Ok = false, At = r.At, Error = r.Error };
                }
            }
            return true;
        }

        static long ToUnixMs(DateTime local)
        {
            return (long)(local.ToUniversalTime() - When.Epoch).TotalMilliseconds;
        }

        // ───────────────────────────── input ─────────────────────────────

        void OnKey(ConsoleKeyInfo k)
        {
            if (k.Key == ConsoleKey.C && (k.Modifiers & ConsoleModifiers.Control) != 0) { quit = true; return; }
            switch (k.Key)
            {
                case ConsoleKey.Enter:
                    string cmd = input.Trim();
                    input = "";
                    histPos = -1;
                    if (cmd.Length > 0)
                    {
                        if (history.Count == 0 || history[history.Count - 1] != cmd) history.Add(cmd);
                        if (history.Count > 50) history.RemoveAt(0);
                        Execute(cmd);
                    }
                    return;
                case ConsoleKey.Backspace:
                    if ((k.Modifiers & ConsoleModifiers.Control) != 0) input = DropWord(input);
                    else if (input.Length > 0) input = input.Substring(0, input.Length - 1);
                    return;
                case ConsoleKey.Tab:
                    Pick((k.Modifiers & ConsoleModifiers.Shift) != 0 ? -1 : 1);
                    return;
                case ConsoleKey.Escape:
                    if (input.Length > 0) input = "";
                    else if (showHelp) showHelp = false;
                    else selKey = null;
                    histPos = -1;
                    return;
                case ConsoleKey.UpArrow:
                    if (history.Count == 0) return;
                    histPos = histPos < 0 ? history.Count - 1 : Math.Max(0, histPos - 1);
                    input = history[histPos];
                    return;
                case ConsoleKey.DownArrow:
                    if (histPos < 0) return;
                    histPos++;
                    if (histPos >= history.Count) { histPos = -1; input = ""; }
                    else input = history[histPos];
                    return;
            }
            if (k.KeyChar >= ' ' && input.Length < 300) input += k.KeyChar;
        }

        // Tab walks through the sessions, then back to "all of them".
        void Pick(int dir)
        {
            showHelp = false;
            if (sessions.Count == 0) { selKey = null; return; }
            int i = selKey == null ? -1 : sessions.FindIndex(s => s.Key == selKey);
            i += dir;
            if (i >= sessions.Count) i = -1;
            else if (i < -1) i = sessions.Count - 1;
            selKey = i < 0 ? null : sessions[i].Key;
        }

        static string DropWord(string s)
        {
            s = s.TrimEnd();
            int i = s.LastIndexOf(' ');
            return i < 0 ? "" : s.Substring(0, i + 1);
        }

        public void Execute(string raw)
        {
            string c = (raw ?? "").Trim();
            showHelp = false;
            if (c.Length == 0) return;

            // "2 25m" · "#2 stop": a command for one session without picking it first
            Match m = NumberFirst.Match(c);
            if (m.Success)
            {
                int n = int.Parse(m.Groups[1].Value);
                string rest = m.Groups[2].Value.Trim();
                if (n >= 1 && n <= sessions.Count)
                {
                    if (ExecuteFor(sessions[n - 1], rest)) return;
                }
                else if (ForOneSession(rest))
                {
                    // otherwise "9 25m" would be read as one 925-minute timer
                    Log(sessions.Count == 0 ? "there are no sessions right now" : "there's no session " + n + " · pick 1-" + sessions.Count + " (or use Tab)", C.Red);
                    return;
                }
            }
            Session sel = Selected();
            if (sel != null && ExecuteFor(sel, c)) return;
            ExecuteAll(c);
        }

        // Would ExecuteFor take this? (a time, +/-, stop, now, auto, t)
        static bool ForOneSession(string c)
        {
            string lc = c.Trim().ToLowerInvariant();
            switch (lc)
            {
                case "stop": case "cancel": case "off": case "disarm": case "now": case "send": case "go":
                case "auto": case "auto on": case "auto off": case "t": case "toggle": case "skip": case "x":
                    return true;
            }
            TimeSpan d; string e;
            if (lc.StartsWith("+") || lc.StartsWith("-")) return When.TryDuration(lc.Substring(1), out d, out e);
            DateTime when; TimeSpan rep; bool clock;
            return When.TryParse(lc, DateTime.Now, out when, out rep, out clock, out e);
        }

        // A command for one session. Returns false, changing nothing, when it isn't one.
        bool ExecuteFor(Session s, string c)
        {
            string lc = c.ToLowerInvariant();
            int sp = lc.IndexOf(' ');
            string verb = sp < 0 ? lc : lc.Substring(0, sp);
            string arg = sp < 0 ? "" : lc.Substring(sp + 1).Trim();
            DateTime now = DateTime.Now;

            switch (verb)
            {
                case "stop": case "cancel": case "off": case "disarm":
                    if (arg.Length > 0) return false;
                    StopOne(s);
                    return true;
                case "now": case "send": case "go":
                    if (arg.Length > 0) return false;
                    if (!s.CanSend) Log("can't reach " + s.Name + " (no inbox key)", C.Red);
                    else Deliver(new List<Session> { s }, "", "");
                    return true;
                case "auto":
                    if (arg.Length > 0 && arg != "on" && arg != "off") return false;
                    AutoOne(s, arg == "on" || (arg != "off" && !AutoFor(s)));
                    return true;
                case "t": case "toggle": case "skip": case "x":
                    if (arg.Length > 0) return false;
                    Toggle(s);
                    return true;
            }

            if (c.StartsWith("+") || c.StartsWith("-"))
            {
                TimeSpan d; string e;
                if (!When.TryDuration(c.Substring(1), out d, out e)) return false;
                AdjustOne(s, c[0] == '+' ? d : d.Negate());
                return true;
            }

            DateTime when; TimeSpan rep; bool clock; string err;
            if (!When.TryParse(c, now, out when, out rep, out clock, out err)) return false;
            PlanFor(s).Own = new Countdown { Target = when, ArmedAt = now, Repeat = rep, IsClock = clock };
            waits.Remove(s.Key);
            bool back = off.Remove(s.Key);
            Log(s.Name + " · its own timer fires at " + When.Stamp(when, now) + " (in " + When.Span(when - now) + ")"
                + (cfg.Loop ? " · then every " + When.Span(rep) : "") + (back ? " · it's back in" : ""), C.Amber);
            return true;
        }

        void ExecuteAll(string c)
        {
            string lc = c.ToLowerInvariant();
            int sp = lc.IndexOf(' ');
            string verb = sp < 0 ? lc : lc.Substring(0, sp);
            string arg = sp < 0 ? "" : c.Substring(sp + 1).Trim();
            string larg = arg.ToLowerInvariant();
            DateTime now = DateTime.Now;

            switch (verb)
            {
                case "q": case "quit": case "exit":
                    quit = true; return;
                case "help": case "?": case "h":
                    showHelp = true; return;
                case "now": case "send": case "go":
                    Deliver(Everyone(), "", ""); return;
                case "stop": case "cancel": case "off": case "disarm":
                    StopAll(); return;
                case "auto":
                    if (larg.Length > 0 && larg != "on" && larg != "off") break;
                    AutoAll(larg == "on" || (larg != "off" && !cfg.Auto));
                    return;
                case "loop": case "repeat":
                    if (larg == "on") cfg.Loop = true; else if (larg == "off") cfg.Loop = false; else cfg.Loop = !cfg.Loop;
                    cfg.Save();
                    Log(cfg.Loop ? "loop on · a timer starts over every time it fires" : "loop off · a timer fires once", C.Text);
                    return;
                case "busy":
                    if (larg == "skip") cfg.SkipBusy = true; else if (larg == "send" || larg == "include") cfg.SkipBusy = false; else cfg.SkipBusy = !cfg.SkipBusy;
                    cfg.Save();
                    Log(cfg.SkipBusy ? "busy sessions will be skipped · only idle ones get the message"
                                     : "busy sessions get it too · it lands in the turn they're running", C.Text);
                    return;
                case "msg": case "message": case "text":
                    cfg.Message = arg.Length > 0 ? arg : "continue";
                    cfg.Save();
                    Log("message set to \"" + Clip(cfg.Message, 60) + "\"", C.Text);
                    return;
                case "t": case "toggle": case "skip": case "x":
                    ToggleSession(larg); return;
                case "all":
                    off.Clear(); Log("every session is included again", C.Text); return;
                case "clear": case "cls":
                    log.Clear(); return;
                case "r": case "refresh": case "list": case "ls":
                    Rescan(); Log("rescanned · " + sessions.Count + " session" + (sessions.Count == 1 ? "" : "s"), C.Muted); return;
            }

            if (c.StartsWith("+") || c.StartsWith("-"))
            {
                TimeSpan d; string e;
                if (main == null)
                {
                    Log(TimerCount() > 0 ? "no main timer to move · pick a session with Tab to move its own" : "no timer running to adjust", C.Muted);
                    return;
                }
                if (!When.TryDuration(c.Substring(1), out d, out e)) { Log(e, C.Red); return; }
                DateTime t = c[0] == '+' ? main.Target + d : main.Target - d;
                if (t <= now.AddSeconds(1)) t = now.AddSeconds(1);
                main.Target = t;
                if (!main.IsClock) main.Repeat = main.Target - main.ArmedAt;
                Log((c[0] == '+' ? "added " : "took off ") + When.Span(d) + " · fires at " + When.Stamp(main.Target, now), C.Text);
                return;
            }

            DateTime when; TimeSpan rep; bool clock; string err;
            if (When.TryParse(c, now, out when, out rep, out clock, out err))
            {
                main = new Countdown { Target = when, ArmedAt = now, Repeat = rep, IsClock = clock };
                firedAt = DateTime.MinValue;
                int own = sessions.Count(s => OwnOf(s) != null);
                Log("armed · fires at " + When.Stamp(when, now) + " (in " + When.Span(when - now) + ")"
                    + (cfg.Loop ? " · then every " + When.Span(rep) : "")
                    + (own > 0 ? " · " + own + (own == 1 ? " session keeps its" : " sessions keep their") + " own timer" : ""), C.Amber);
                return;
            }
            Log(err ?? ("didn't understand \"" + c + "\" (type help)"), C.Red);
        }

        void StopOne(Session s)
        {
            Plan p;
            bool had = plans.TryGetValue(s.Key, out p) && p.Own != null;
            if (had) p.Own = null;
            if (waits.Remove(s.Key)) had = true;
            bool followsMain = main != null && !off.Contains(s.Key);
            if (!had)
            {
                Log(s.Name + " has no timer of its own" + (followsMain ? " · it follows the main timer (t leaves it out)" : ""), C.Muted);
                return;
            }
            LimitHit h;
            if (hits.TryGetValue(s.Key, out h)) answered.Add(h.Id);   // auto leaves the stop it's on alone from now on
            Log(s.Name + " · timer stopped" + (AutoFor(s) ? " · auto stays on for its next usage limit" : "")
                + (followsMain ? " · it follows the main timer again" : ""), C.Muted);
        }

        void StopAll()
        {
            int n = main != null ? 1 : 0;
            main = null;
            foreach (Session s in sessions)
            {
                Plan p;
                bool had = plans.TryGetValue(s.Key, out p) && p.Own != null;
                if (had) p.Own = null;
                if (waits.Remove(s.Key)) had = true;
                if (!had) continue;
                n++;
                LimitHit h;
                if (hits.TryGetValue(s.Key, out h)) answered.Add(h.Id);
            }
            if (n == 0) { Log("no timer running", C.Muted); return; }
            Log((n == 1 ? "timer stopped" : "all " + n + " timers stopped")
                + (sessions.Any(s => AutoFor(s)) ? " · auto stays on for the next usage limit (auto off turns it off)" : ""), C.Muted);
        }

        void AdjustOne(Session s, TimeSpan d)
        {
            DateTime now = DateTime.Now;
            Countdown own = OwnOf(s);
            AutoWait w;
            if (own == null && waits.TryGetValue(s.Key, out w))
            {
                // moving an auto timer makes it the session's own
                own = new Countdown { Target = w.Fire, ArmedAt = w.Since, Repeat = w.Fire - w.Since };
                PlanFor(s).Own = own;
                waits.Remove(s.Key);
            }
            if (own == null)
            {
                Log(s.Name + " has no timer of its own to move · type one for it, like 25m", C.Muted);
                return;
            }
            DateTime t = own.Target + d;
            if (t <= now.AddSeconds(1)) t = now.AddSeconds(1);
            own.Target = t;
            if (!own.IsClock) own.Repeat = t - own.ArmedAt;
            Log(s.Name + " · " + (d >= TimeSpan.Zero ? "added " : "took off ") + When.Span(d.Duration()) + " · fires at " + When.Stamp(t, now), C.Text);
        }

        void AutoAll(bool on)
        {
            cfg.Auto = on;
            cfg.Save();
            foreach (Plan p in plans.Values) p.Auto = null;   // one switch for everyone again
            if (!on)
            {
                int n = waits.Count;
                waits.Clear();
                Log("auto off · usage limits are left alone" + (n > 0 ? " · " + n + " auto timer" + (n == 1 ? "" : "s") + " dropped" : ""), C.Text);
                return;
            }
            Log("auto on · a session stopped by a usage limit continues " + AfterReset("the limit"), C.Text);
            UpdateAuto();
            ExplainLimits(null);
        }

        void AutoOne(Session s, bool on)
        {
            PlanFor(s).Auto = on;
            if (!on)
            {
                bool had = waits.Remove(s.Key);
                Log(s.Name + " · auto off" + (had ? " · its auto timer is dropped" : "") + (cfg.Auto ? " · the others keep auto" : ""), C.Text);
                return;
            }
            bool back = off.Remove(s.Key);
            Log(s.Name + " · auto on · it continues " + AfterReset("its usage limit") + (back ? " · it's back in" : ""), C.Text);
            UpdateAuto();
            ExplainLimits(s);
        }

        // "1m after the limit resets" (automargin=60) · "as soon as the limit resets" (automargin=0)
        string AfterReset(string what)
        {
            return cfg.AutoMarginSec > 0 ? When.Span(TimeSpan.FromSeconds(cfg.AutoMarginSec)) + " after " + what + " resets"
                                         : "as soon as " + what + " resets";
        }

        // After auto is switched on: why a session that's on a usage limit won't be continued by itself (or that none is).
        void ExplainLimits(Session only)
        {
            DateTime now = DateTime.Now;
            bool any = false;
            foreach (Session s in sessions)
            {
                if (only != null && s.Key != only.Key) continue;
                LimitHit h;
                if (!hits.TryGetValue(s.Key, out h)) continue;
                any = true;
                if (waits.ContainsKey(s.Key)) continue;
                string why = OwnOf(s) != null ? "its own timer goes first"
                    : off.Contains(s.Key) ? "it's left out"
                    : !s.CanSend ? "it can't be reached"
                    : !h.Known ? "its reset time can't be read"
                    : WorkingAgain(s, h) ? "it's already working again"
                    : answered.Contains(h.Id) ? "it already got the message after that stop"
                    : h.ResetsAt < now - TooOld ? "that limit reset " + When.Span(now - h.ResetsAt) + " ago, too long for auto"
                    : null;
                if (why != null)
                    Log("  " + s.Name + " is on its " + h.Kind + " limit, but " + why + " · " + (sessions.IndexOf(s) + 1) + " now sends right away", C.Muted);
            }
            if (!any) Log("  " + (only != null ? only.Name + " isn't" : "no session is") + " stopped on a usage limit right now · watching", C.Muted);
        }

        void Toggle(Session s)
        {
            if (off.Remove(s.Key)) { Log(s.Name + " is back in", C.Text); return; }
            off.Add(s.Key);
            waits.Remove(s.Key);
            Log(s.Name + " left out · no timer sends to it (t " + (sessions.IndexOf(s) + 1) + " again puts it back)", C.Text);
        }

        void ToggleSession(string arg)
        {
            int n;
            if (!int.TryParse(arg.TrimStart('#'), out n) || n < 1 || n > sessions.Count)
            {
                Log(sessions.Count == 0 ? "there are no sessions to pick from" : "pick a session number 1-" + sessions.Count + ", e.g.  t 2", C.Red);
                return;
            }
            Toggle(sessions[n - 1]);
        }

        static readonly string[][] HelpRows =
        {
            new[] { "timer", "25m  ·  90s  ·  1h30  ·  2h15m  ·  1:30:00  ·  45 (= minutes)" },
            new[] { "clock", "at 15:30  ·  15:30  ·  3pm  ·  9:45am  (the next time it comes)" },
            new[] { "adjust", "+10m  ·  -5m  ·  stop (stops every timer)" },
            new[] { "pick", "Tab / Shift+Tab picks a session: what you type then is for it alone · Esc = all" },
            new[] { "2 25m", "the same without picking: 2 25m · 2 at 15:30 · 2 +5m · 2 stop · 2 now · 2 auto" },
            new[] { "auto", "a session stopped by a usage limit continues by itself when the limit resets" },
            new[] { "now", "send it right away (running timers keep going)" },
            new[] { "msg", "msg keep going  →  changes the text  ·  msg  →  back to \"continue\"" },
            new[] { "loop", "start a timer over every time it fires" },
            new[] { "busy", "skip sessions that are busy, or include them (toggle)" },
            new[] { "t 2", "leave session 2 out  ·  t 2 again puts it back  ·  all" },
            new[] { "other", "refresh  ·  clear  ·  quit or Ctrl+C  ·  ↑/↓ history" },
        };

        void Log(string text, Rgb color)
        {
            log.Add(new LogEntry { At = DateTime.Now, Text = text, Color = color });
            if (log.Count > 300) log.RemoveRange(0, log.Count - 300);
        }

        static string Clip(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, Math.Max(1, n - 1)) + "…";
        }

        // ───────────────────────────── drawing ─────────────────────────────

        static readonly Dictionary<char, string[]> Font = new Dictionary<char, string[]>
        {
            { '0', new[] { "###", "# #", "# #", "# #", "###" } },
            { '1', new[] { " # ", "## ", " # ", " # ", "###" } },
            { '2', new[] { "###", "  #", "###", "#  ", "###" } },
            { '3', new[] { "###", "  #", " ##", "  #", "###" } },
            { '4', new[] { "# #", "# #", "###", "  #", "  #" } },
            { '5', new[] { "###", "#  ", "###", "  #", "###" } },
            { '6', new[] { "###", "#  ", "###", "# #", "###" } },
            { '7', new[] { "###", "  #", "  #", "  #", "  #" } },
            { '8', new[] { "###", "# #", "###", "# #", "###" } },
            { '9', new[] { "###", "# #", "###", "  #", "###" } },
            { '-', new[] { "   ", "   ", "###", "   ", "   " } },
            { ':', new[] { " ", "#", " ", "#", " " } },
        };

        const int BigWidth = 54; // 6 digits x 6 + 2 colons x 2 + 7 gaps x 2

        // The session's own timer, or its auto timer (null when it only follows the main timer).
        Focus PersonalFocus(Session s)
        {
            Countdown own = OwnOf(s);
            if (own != null) return new Focus { S = s, Timer = own, Target = own.Target, From = own.ArmedAt };
            AutoWait w;
            if (!waits.TryGetValue(s.Key, out w)) return null;
            DateTime from = w.Hit.At != DateTime.MinValue && w.Hit.At < w.Fire ? w.Hit.At : w.Since;
            return new Focus { S = s, Wait = w, Target = w.Fire, From = from };
        }

        // What the big clock shows: the picked session's timer, otherwise whichever timer fires first.
        Focus GetFocus()
        {
            Session sel = Selected();
            if (sel != null)
            {
                Focus mine = PersonalFocus(sel);
                if (mine != null) return mine;
                if (main == null || off.Contains(sel.Key) || !sel.CanSend) return null;
                return new Focus { S = sel, Timer = main, Main = true, Target = main.Target, From = main.ArmedAt };
            }
            Focus best = main == null ? null : new Focus { Timer = main, Main = true, Target = main.Target, From = main.ArmedAt };
            foreach (Session s in sessions)
            {
                Focus f = PersonalFocus(s);
                if (f != null && (best == null || f.Target < best.Target)) best = f;
            }
            return best;
        }

        string ClockText(Focus f, DateTime now)
        {
            if (f != null)
            {
                TimeSpan left = f.Target - now;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                long s = (long)Math.Ceiling(left.TotalSeconds - 0.001);
                if (s > 99 * 3600 + 3599) s = 99 * 3600 + 3599;
                return (s / 3600).ToString("00") + ":" + ((s % 3600) / 60).ToString("00") + ":" + (s % 60).ToString("00");
            }
            return firedAt != DateTime.MinValue && selKey == null ? "00:00:00" : "--:--:--";
        }

        Rgb ClockColor(Focus f, DateTime now)
        {
            if (f != null)
            {
                if ((now - firedAt).TotalSeconds < 3) return C.Green;          // just fired, something else is still armed
                return (f.Target - now).TotalSeconds <= 10 ? C.Ember : C.Amber;
            }
            return firedAt != DateTime.MinValue && selKey == null ? C.Green : C.Dim;
        }

        void BigClock(List<Line> L, int W, DateTime now, Focus f)
        {
            string text = ClockText(f, now);
            Rgb lit = ClockColor(f, now);
            bool colonOn = f == null || now.Millisecond < 500;
            int pad = Math.Max(0, (W - BigWidth) / 2);
            for (int row = 0; row < 5; row++)
            {
                var ln = new Line(W).Gap(pad);
                for (int i = 0; i < text.Length; i++)
                {
                    if (i > 0) ln.Gap(2);
                    char ch = text[i];
                    string[] g = Font[ch];
                    foreach (char px in g[row])
                    {
                        if (ch == ':') ln.Add(px == '#' && colonOn ? "██" : "  ", lit);
                        else if (px == '#') ln.Add("██", lit);
                        else ln.Ghost("██", C.Unlit);
                    }
                }
                L.Add(ln);
            }
        }

        public List<string> PlainFrame(int w, int h)
        {
            return Build(w, h).Select(l => l.Plain).ToList();
        }

        List<Line> Build(int W, int H)
        {
            DateTime now = DateTime.Now;
            var L = new List<Line>();
            Focus f = GetFocus();
            Session sel = Selected();

            if (W < 44 || H < 12)
            {
                var tiny = new Line(W).Add(" NudgeClock ", C.Amber).Add(ClockText(f, now), ClockColor(f, now));
                L.Add(tiny);
                L.Add(new Line(W).Add(" make the window bigger", C.Muted));
                return L;
            }

            // header
            L.Add(new Line(W).Add(" ", C.Text).Bold("NUDGECLOCK", C.Amber).Add("   auto-continue for Claude Code", C.Muted)
                .Right(now.ToString("ddd HH:mm:ss") + " ", C.Muted));
            L.Add(Rule(W));

            int sessRows = Math.Max(1, sessions.Count);
            int fixedRows = 2 + 1 + 1 + 1 + 2 + 1 + 1 + 1 + 1 + 3; // without the clock body, session rows and log rows
            bool big = W >= BigWidth + 4 && H >= fixedRows + 5 + sessRows + 3;
            int clockRows = big ? 5 : 1;
            int maxSess = Math.Max(1, H - fixedRows - clockRows - 2);
            bool clipSess = sessRows > maxSess;
            if (clipSess) sessRows = maxSess;

            // clock
            L.Add(new Line(W));
            if (big) BigClock(L, W, now, f);
            else
            {
                string t = ClockText(f, now);
                L.Add(new Line(W).Gap(Math.Max(0, (W - t.Length) / 2)).Bold(t, ClockColor(f, now)));
            }
            L.Add(new Line(W));

            // status + progress
            StatusLines(L, W, now, f, sel);
            L.Add(new Line(W));

            if (showHelp)
            {
                L.Add(new Line(W).Add(" ", C.Text).Bold("HELP", C.Text).Add("  type a command and press Enter · Esc closes this", C.Muted));
                foreach (string[] row in HelpRows)
                {
                    if (L.Count >= H - 3) break;
                    L.Add(new Line(W).Gap(2).Add(row[0].PadRight(10), C.Amber).Add(row[1], C.Text));
                }
                while (L.Count < H - 3) L.Add(new Line(W));
                AddPrompt(L, W, now, sel);
                return L;
            }

            // sessions
            int inCount = sessions.Count(s => !off.Contains(s.Key) && s.CanSend);
            var sh = new Line(W).Add(" ", C.Text).Bold("SESSIONS", C.Text)
                .Add("  " + inCount + " of " + sessions.Count + " in" + (Only != null ? " (--only filter)" : "")
                     + (sessions.Count > 0 ? " · Tab picks one" : ""), C.Muted);
            int before = sh.Len;
            sh.Right("msg \"" + Clip(cfg.Message, 18) + "\" · loop " + (cfg.Loop ? "on" : "off") + " · busy: " + (cfg.SkipBusy ? "skip" : "send")
                + " · auto " + (cfg.Auto ? "on" : "off") + " ", C.Muted);
            if (sh.Len == before) sh.Right("auto " + (cfg.Auto ? "on" : "off") + " ", C.Muted);
            L.Add(sh);

            if (sessions.Count == 0)
            {
                L.Add(new Line(W).Gap(4).Add(scanError ?? ("no Claude Code sessions running · watching " + Claude.SessionsDir()), C.Muted));
            }
            else
            {
                int nameW = Math.Min(24, Math.Max(12, sessions.Max(s => s.Name.Length) + 2));
                int used = 3 + 2 + 1 + 2 + nameW + 9 + 9 + 6;
                // the project column only earns its space when sessions are spread over several folders (and there's room)
                bool manyProjects = sessions.Select(s => s.Project.ToLowerInvariant()).Distinct().Count() > 1;
                int markW = marks.Count > 0 ? 16 : 0;   // "✓ sent 02:00:11" after something was sent
                int projW = manyProjects && W - used >= 20 + 46 + 16 ? 20 : 0;
                int planW = Math.Min(46, W - used - projW - markW);
                if (planW < 20) planW = 0;
                int show = clipSess ? sessRows - 1 : sessions.Count;
                int selIdx = sel != null ? sessions.IndexOf(sel) : -1;
                int firstRow = selIdx >= show ? selIdx - show + 1 : 0;   // keep the picked one in view
                for (int i = firstRow; i < firstRow + show; i++) L.Add(SessionRow(W, i, sessions[i], nameW, projW, planW, now));
                if (clipSess) L.Add(new Line(W).Gap(6).Add("+" + (sessions.Count - show) + " more (make the window taller)", C.Muted));
            }
            L.Add(new Line(W));

            // log
            L.Add(new Line(W).Add(" ", C.Text).Bold("LOG", C.Text));
            int logRows = Math.Max(0, H - L.Count - 3);
            int start = Math.Max(0, log.Count - logRows);
            for (int i = start; i < log.Count; i++)
            {
                LogEntry e = log[i];
                L.Add(new Line(W).Gap(2).Add(e.At.ToString("HH:mm:ss"), C.Dim).Gap(2).Add(e.Text, e.Color));
            }
            while (L.Count < H - 3) L.Add(new Line(W));
            AddPrompt(L, W, now, sel);
            return L;
        }

        void StatusLines(List<Line> L, int W, DateTime now, Focus f, Session sel)
        {
            var st = new Line(W).Gap(2);
            var pr = new Line(W).Gap(2);
            if (sending > 0)
            {
                st.Bold("● SENDING ", C.Amber).Add(" delivering \"" + Clip(cfg.Message, 30) + "\"…", C.Text);
            }
            else if (f != null)
            {
                TimeSpan total = f.Target - f.From, left = f.Target - now;
                st.Bold(f.Wait != null ? "● AUTO " : "● ARMED ", C.Amber);
                if (f.S != null)
                    st.Add("  ", C.Text).Add(Clip(f.S.Name, 24), C.Text)
                      .Add(f.Main ? "  ·  main timer" : f.Wait == null ? "  ·  own timer" : "", C.Muted).Add("  ·", C.Muted);
                st.Add("  fires at ", C.Muted).Add(When.Stamp(f.Target, now), C.Text)
                  .Add("  ·  in ", C.Muted).Add(When.Span(left), C.Text);
                if (f.Wait != null)
                {
                    LimitHit h = f.Wait.Hit;
                    st.Add("  ·  " + h.Kind + " limit " + (h.ResetsAt > now ? "resets " + When.Short(h.ResetsAt, now) : "has reset"), C.Muted);
                }
                else if (f.S == null)
                {
                    int n = MainTargets().Count;
                    st.Add("  ·  ", C.Muted).Add(n + " session" + (n == 1 ? "" : "s"), C.Text);
                }
                if (f.Timer != null && cfg.Loop) st.Add("  ·  then every " + When.Span(f.Timer.Repeat), C.Muted);

                int bw = Math.Max(10, Math.Min(W - 44, 56));
                double frac = total.TotalSeconds <= 0 ? 1 : Math.Max(0, Math.Min(1, 1 - left.TotalSeconds / total.TotalSeconds));
                int filled = (int)Math.Round(frac * bw);
                pr.Add(new string('█', filled), C.Amber).Add(new string('░', bw - filled), C.Dim).Gap(2)
                  .Add(((int)(frac * 100)).ToString().PadLeft(3) + "%", C.Muted).Add("   PC kept awake", C.Dim);
                int others = TimerCount() - 1;
                if (others > 0) pr.Add("  ·  " + others + " more timer" + (others == 1 ? "" : "s"), C.Muted);
            }
            else if (sel != null)
            {
                st.Bold("○ ", C.Muted).Add(Clip(sel.Name, 24), C.Text).Add("  no timer for it yet · type one, like ", C.Muted)
                  .Add("25m", C.Text).Add(" or ", C.Muted).Add("at 15:30", C.Text);
                pr.Add(AutoFor(sel) ? "auto is on for it · it continues by itself after a usage limit"
                                    : "or type auto: it then continues by itself after a usage limit", C.Dim);
            }
            else if (firedAt != DateTime.MinValue)
            {
                st.Bold("✓ FIRED ", C.Green).Add("  at " + When.Stamp(firedAt, now), C.Muted).Add("  ·  " + firedSummary, C.Text);
                pr.Add(cfg.Auto ? "auto on · watching for usage limits · or type a new timer" : "type a new timer to go again", C.Dim);
            }
            else
            {
                st.Bold("○ IDLE ", C.Muted).Add("  no timer yet · type one below, like ", C.Muted)
                  .Add("25m", C.Text).Add(", ", C.Muted).Add("1h30", C.Text).Add(" or ", C.Muted).Add("at 15:30", C.Text);
                pr.Add(cfg.Auto ? "auto on · a session stopped by a usage limit continues by itself when the limit resets"
                                : "when it hits zero, every session below gets \"" + Clip(cfg.Message, 30) + "\" · Tab picks just one", C.Dim);
            }
            L.Add(st);
            L.Add(pr);
        }

        void AddPrompt(List<Line> L, int W, DateTime now, Session sel)
        {
            L.Add(Rule(W));
            var inp = new Line(W).Add(" ", C.Text).Bold("›", C.Amber).Gap(1);
            if (sel != null) inp.Bold("[" + (sessions.IndexOf(sel) + 1) + " " + Clip(sel.Name, 22) + "]", C.Amber).Gap(1);
            int room = Math.Max(1, W - inp.Len - 2);
            string shown = input.Length > room ? input.Substring(input.Length - room) : input;
            inp.Add(shown, C.Text);
            inp.Add(now.Millisecond < 530 ? "█" : " ", C.Amber);
            if (input.Length == 0)
                inp.Gap(1).Add(sel != null ? "a time just for " + sel.Name + ", or auto · now · stop · Esc = all" : "type a timer and press Enter", C.Dim);
            L.Add(inp);

            var hint = new Line(W).Gap(1);
            string[] hints = showHelp ? new[] { "Esc closes help" }
                : sel != null ? new[] { "25m", "at 15:30", "+5m", "now", "stop", "auto", "t leaves it out", "Tab next", "Esc all sessions" }
                : new[] { "help", "25m", "1h30", "at 15:30", "Tab picks a session", "auto", "now", "stop", "msg <text>", "loop", "busy", "+5m", "quit" };
            for (int i = 0; i < hints.Length; i++)
            {
                string sep = i > 0 ? " · " : "";
                if (hint.Len + sep.Length + hints[i].Length > W) break;
                hint.Add(sep, C.Dim).Add(hints[i], i == 0 && !showHelp && sel == null ? C.Text : C.Muted);
            }
            L.Add(hint);
        }

        Line SessionRow(int W, int i, Session s, int nameW, int projW, int planW, DateTime now)
        {
            bool picked = s.Key == selKey;
            bool included = !off.Contains(s.Key) && s.CanSend && !(cfg.SkipBusy && s.Busy);
            Rgb stColor = s.Busy ? C.Cyan : s.Status == "idle" ? C.Green : C.Warn;
            var ln = new Line(W).Add(picked ? "►" : " ", C.Amber)
                .Add((i + 1).ToString().PadLeft(2), picked ? C.Amber : C.Muted).Gap(2)
                .Add(included ? "●" : "○", included ? stColor : C.Dim).Gap(2);
            string name = Clip(s.Name, nameW - 2).PadRight(nameW);
            if (picked) ln.Bold(name, C.Amber); else ln.Add(name, included ? C.Text : C.Muted);
            ln.Add(Clip(s.Status, 8).PadRight(9), included ? stColor : C.Dim)
              .Add(s.Source.PadRight(9), C.Muted)
              .Add(When.Age(s.StartedAt).PadRight(6), C.Dim);
            if (projW > 0) ln.Add(Clip(s.Project, projW - 2).PadRight(projW), C.Dim);

            Rgb pc;
            string plan = PlanText(s, now, out pc);
            if (planW > 0) ln.Add(Clip(plan, planW - 2).PadRight(planW), pc);
            else if (plan.Length > 0) ln.Add(plan + "   ", pc);

            Mark m;
            if (marks.TryGetValue(s.Key, out m))
            {
                if (m.Ok) ln.Add("✓ sent " + m.At.ToString("HH:mm:ss"), C.Green);
                else ln.Add("× " + m.Error, C.Red);
            }
            return ln;
        }

        // The session's own column: what sends to it next, or why nothing will.
        string PlanText(Session s, DateTime now, out Rgb color)
        {
            color = C.Dim;
            if (!s.CanSend) { color = C.Red; return "can't reach it (no inbox key)"; }
            if (off.Contains(s.Key)) { color = C.Muted; return "left out"; }
            Countdown own = OwnOf(s);
            if (own != null) { color = C.Amber; return "own timer " + When.Stamp(own.Target, now) + " · " + When.Span(own.Target - now); }
            AutoWait w;
            if (waits.TryGetValue(s.Key, out w))
            {
                color = C.Amber;
                return "auto " + When.Stamp(w.Fire, now) + " · " + When.Span(w.Fire - now) + " · " + w.Hit.Kind + " limit";
            }
            LimitHit h;
            if (hits.TryGetValue(s.Key, out h))
            {
                color = C.Warn;
                string reset = !h.Known ? "reset time unknown"
                    : h.ResetsAt > now ? "resets " + When.Short(h.ResetsAt, now)
                    : "reset " + When.Span(now - h.ResetsAt) + " ago";
                if (main != null && !(cfg.SkipBusy && s.Busy)) return "main " + When.Stamp(main.Target, now) + " · limit " + reset;
                return h.Kind + " limit · " + reset;
            }
            if (cfg.SkipBusy && s.Busy) { color = C.Muted; return "skipped (busy)"; }
            var parts = new List<string>();
            if (main != null) parts.Add("main timer " + When.Stamp(main.Target, now));
            if (AutoFor(s)) parts.Add("auto");
            return string.Join(" · ", parts);
        }

        static Line Rule(int W)
        {
            return new Line(W).Add(new string('─', W), C.Dim);
        }
    }
}
