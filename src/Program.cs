using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace NudgeClock
{
    static class Program
    {
        static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            string first = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            if (first == "--help" || first == "-h" || first == "/?") { Usage(); return 0; }
            if (first == "--list") return List();
            if (first == "--send") return Send(args);
            if (first == "--frame") return Frame(args);

            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                Console.Error.WriteLine("NudgeClock needs a terminal window. For scripts use --list or --send.");
                return 2;
            }
            var words = new List<string>();
            string only = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--only" && i + 1 < args.Length) only = args[++i];
                else words.Add(args[i]);
            }
            var app = new App(Settings.Load());
            if (only != null) app.Only = ParseOnly(only);
            return app.Run(string.Join(" ", words).Trim());
        }

        public static HashSet<string> ParseOnly(string list)
        {
            return new HashSet<string>(list.Split(',').Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0));
        }

        public static bool Matches(HashSet<string> keys, Session s)
        {
            return keys.Contains(s.Pid.ToString()) || keys.Contains((s.Name ?? "").ToLowerInvariant())
                || keys.Contains((s.SessionId ?? "").ToLowerInvariant());
        }

        static void Usage()
        {
            Console.WriteLine("NudgeClock - sends \"continue\" to your running Claude Code sessions when a timer runs out.");
            Console.WriteLine();
            Console.WriteLine("  NudgeClock                       open the terminal UI");
            Console.WriteLine("  NudgeClock 2h | at 15:30 | 3pm   open it with a timer already running");
            Console.WriteLine("  NudgeClock auto on               open it with auto mode on (continues sessions when their usage limit resets)");
            Console.WriteLine("  NudgeClock --only <pid|name>,... [timer]   only watch the sessions named");
            Console.WriteLine("  NudgeClock --list                print the sessions it can see, and any usage limit they're stopped on");
            Console.WriteLine("  NudgeClock --send [text] [--only <pid|name>,...]   send right now and exit");
        }

        static int List()
        {
            string err;
            List<Session> all = Claude.Scan(out err);
            if (err != null) Console.WriteLine(err);
            var limits = new Limits();
            DateTime now = DateTime.Now;
            foreach (Session s in all)
            {
                LimitHit h = s.Alive ? limits.Check(s) : null;
                string lim = h == null ? "-" : h.Kind + " limit, " + (!h.Known ? "reset unknown"
                    : (h.ResetsAt > now ? "resets " : "reset ") + h.ResetsAt.ToString("ddd HH:mm"));
                Console.WriteLine("{0,-7} {1,-24} {2,-6} {3,-9} {4,-5} {5,-8} {6,-32} {7}",
                    s.Pid, s.Name, s.Status, s.Source, s.Alive ? "alive" : "dead", s.CanSend ? "inbox" : "no-key", lim, s.Cwd);
            }
            Console.WriteLine("{0} live session(s) in {1}", all.Count(s => s.Alive), Claude.SessionsDir());
            return 0;
        }

        static int Send(string[] args)
        {
            var words = new List<string>();
            string only = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--only" && i + 1 < args.Length) only = args[++i];
                else words.Add(args[i]);
            }
            string text = words.Count > 0 ? string.Join(" ", words) : Settings.Load().Message;

            string err;
            List<Session> targets = Claude.Scan(out err).Where(s => s.Alive && s.CanSend).ToList();
            if (only != null)
            {
                HashSet<string> keys = ParseOnly(only);
                targets = targets.Where(s => Matches(keys, s)).ToList();
            }
            if (targets.Count == 0) { Console.WriteLine("no matching live sessions" + (err != null ? " (" + err + ")" : "")); return 1; }

            int fails = 0;
            foreach (Session s in targets)
            {
                string e = Claude.Send(s, text);
                Console.WriteLine("{0} {1,-24} {2}", e == null ? "ok  " : "FAIL", s.Name, e ?? "delivered");
                if (e != null) fails++;
            }
            return fails == 0 ? 0 : 1;
        }

        // --frame WxH [command ...]: prints one frame as plain text after running the commands (dry run, nothing is sent).
        // Test helpers among the commands: "wait 25" lets 25 seconds pass; {tab} {shift+tab} {esc} press those keys.
        static int Frame(string[] args)
        {
            int w = 100, h = 34;
            if (args.Length > 1)
            {
                string[] p = args[1].ToLowerInvariant().Split('x');
                if (p.Length == 2) { int.TryParse(p[0], out w); int.TryParse(p[1], out h); }
            }
            Settings cfg = Settings.Load();
            cfg.NoSave = true;
            var app = new App(cfg);
            app.DryRun = true;
            app.Execute("refresh");
            for (int i = 2; i < args.Length; i++)
            {
                string a = args[i].Trim(), la = a.ToLowerInvariant();
                double secs;
                if (la.StartsWith("wait ") && double.TryParse(la.Substring(5), NumberStyles.Float, CultureInfo.InvariantCulture, out secs)) app.Advance(secs);
                else if (la == "{tab}") app.Press(ConsoleKey.Tab, false);
                else if (la == "{shift+tab}") app.Press(ConsoleKey.Tab, true);
                else if (la == "{esc}") app.Press(ConsoleKey.Escape, false);
                else app.Execute(a);
            }
            foreach (string line in app.PlainFrame(w, h)) Console.WriteLine(line);
            return 0;
        }
    }
}
