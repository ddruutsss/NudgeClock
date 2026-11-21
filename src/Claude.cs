using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace NudgeClock
{
    // One running Claude Code session, as described by ~/.claude/sessions/<pid>.json
    class Session
    {
        public int Pid;
        public string SessionId, Name, Status, Kind, Entrypoint, Cwd, PipePath, Token, Version;
        public long StartedAt, StatusUpdatedAt; // unix ms
        public bool Alive;

        public bool CanSend { get { return !string.IsNullOrEmpty(Token) && !string.IsNullOrEmpty(PipePath); } }
        public bool Busy { get { return Status == "busy"; } }

        // One per window/terminal: the process stays the same when its conversation is cleared or switched.
        public string Key { get { return Pid.ToString(); } }

        public string Source
        {
            get
            {
                string e = Entrypoint ?? "";
                if (e.IndexOf("vscode", StringComparison.OrdinalIgnoreCase) >= 0) return "vscode";
                if (e.IndexOf("jetbrains", StringComparison.OrdinalIgnoreCase) >= 0) return "jetbrains";
                if (e == "cli" || e.Length == 0) return "terminal";
                return e.Replace("claude-", "");
            }
        }

        public string Project
        {
            get
            {
                if (string.IsNullOrEmpty(Cwd)) return "";
                string c = Cwd.TrimEnd('\\', '/');
                int i = Math.Max(c.LastIndexOf('\\'), c.LastIndexOf('/'));
                return i >= 0 ? c.Substring(i + 1) : c;
            }
        }

        // "\\.\pipe\LOCAL\cc-msg-..." -> "LOCAL\cc-msg-..."
        public string PipeName
        {
            get
            {
                string p = PipePath ?? "";
                const string pre = @"\\.\pipe\";
                return p.StartsWith(pre, StringComparison.OrdinalIgnoreCase) ? p.Substring(pre.Length) : p;
            }
        }
    }

    static class Claude
    {
        public const string Sender = "NudgeClock";

        static readonly Regex JsonFile = new Regex(@"^(\d+)\.json$");
        static readonly Regex KeyFile = new Regex(@"^(\d+)\.[0-9a-f]{64}\.key$");
        static readonly Regex Hex32 = new Regex(@"^[0-9a-f]{32}$");

        // %USERPROFILE%\.claude, or CLAUDE_CONFIG_DIR when it's set
        public static string Root()
        {
            string cfg = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return !string.IsNullOrEmpty(cfg) ? cfg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }

        public static string SessionsDir()
        {
            return Path.Combine(Root(), "sessions");
        }

        // Reads every session record plus its inbox key. Dead records (process gone) come back with Alive=false.
        public static List<Session> Scan(out string error)
        {
            error = null;
            var list = new List<Session>();
            string dir = SessionsDir();
            if (!Directory.Exists(dir)) { error = "no sessions folder at " + dir; return list; }

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (Exception ex) { error = ex.Message; return list; }

            var tokens = new Dictionary<int, string>();
            var tokenTimes = new Dictionary<int, DateTime>();
            foreach (string f in files)
            {
                Match m = KeyFile.Match(Path.GetFileName(f));
                if (!m.Success) continue;
                int pid = int.Parse(m.Groups[1].Value);
                DateTime t;
                try { t = File.GetLastWriteTimeUtc(f); } catch { continue; }
                DateTime prev;
                if (tokenTimes.TryGetValue(pid, out prev) && prev >= t) continue;
                string tok = ReadToken(f);
                if (tok == null) continue;
                tokens[pid] = tok;
                tokenTimes[pid] = t;
            }

            foreach (string f in files)
            {
                Match m = JsonFile.Match(Path.GetFileName(f));
                if (!m.Success) continue;
                Dictionary<string, object> d = ReadJson(f);
                if (d == null) continue;
                var s = new Session();
                s.Pid = (int)Num(d, "pid", long.Parse(m.Groups[1].Value));
                s.SessionId = Str(d, "sessionId");
                s.Name = Str(d, "name");
                s.Status = Str(d, "status") ?? "?";
                s.Kind = Str(d, "kind");
                s.Entrypoint = Str(d, "entrypoint");
                s.Cwd = Str(d, "cwd");
                s.PipePath = Str(d, "messagingSocketPath");
                s.Version = Str(d, "version");
                s.StartedAt = Num(d, "startedAt", 0);
                s.StatusUpdatedAt = Num(d, "statusUpdatedAt", Num(d, "updatedAt", 0));
                if (string.IsNullOrEmpty(s.Name)) s.Name = "session-" + s.Pid;
                string tok;
                if (tokens.TryGetValue(s.Pid, out tok)) s.Token = tok;
                s.Alive = IsAlive(s.Pid, Str(d, "procStart"));
                list.Add(s);
            }
            list.Sort(delegate(Session a, Session b) { return a.StartedAt.CompareTo(b.StartedAt); });
            return list;
        }

        // Delivers `text` into the session's prompt queue via its local inbox pipe.
        // Returns null on success, otherwise a short reason.
        public static string Send(Session s, string text)
        {
            if (string.IsNullOrEmpty(s.Token)) return "no inbox key for this session";
            if (string.IsNullOrEmpty(s.PipePath)) return "session has no inbox";

            var ser = new JavaScriptSerializer();
            var auth = new Dictionary<string, object>();
            auth["type"] = "auth";
            auth["token"] = s.Token;

            var inner = new Dictionary<string, object>();
            inner["role"] = "user";
            inner["content"] = text;
            var msg = new Dictionary<string, object>();
            msg["type"] = "user";
            msg["message"] = inner;
            if (!string.IsNullOrEmpty(s.SessionId)) msg["session_id"] = s.SessionId;
            msg["priority"] = "next";
            msg["from"] = Sender;
            msg["uuid"] = Guid.NewGuid().ToString();

            byte[] bytes = new UTF8Encoding(false).GetBytes(ser.Serialize(auth) + "\n" + ser.Serialize(msg) + "\n");
            try
            {
                using (var pipe = new NamedPipeClientStream(".", s.PipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    pipe.Connect(3000);
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();
                    try { pipe.WaitForPipeDrain(); } catch { }
                    Thread.Sleep(200);
                }
                return null;
            }
            catch (TimeoutException) { return "inbox not answering"; }
            catch (IOException ex) { return Short(ex.Message); }
            catch (UnauthorizedAccessException) { return "access denied"; }
            catch (Exception ex) { return Short(ex.Message); }
        }

        static string Short(string m)
        {
            m = (m ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return m.Length > 60 ? m.Substring(0, 60) : m;
        }

        static string ReadToken(string path)
        {
            Dictionary<string, object> d = ReadJson(path);
            if (d == null) return null;
            string t = Str(d, "peerToken");
            return t != null && Hex32.IsMatch(t) ? t : null;
        }

        static Dictionary<string, object> ReadJson(string path)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    string text;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(fs, Encoding.UTF8))
                        text = r.ReadToEnd();
                    if (text.Length > 65536) return null;
                    return new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                }
                catch { Thread.Sleep(25); }
            }
            return null;
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return null;
            return v as string ?? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        static long Num(Dictionary<string, object> d, string k, long def)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return def;
            try { return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return def; }
        }

        // The pid must exist and (when known) have the same start time, so a recycled pid isn't mistaken for the session.
        static bool IsAlive(int pid, string procStart)
        {
            Process p;
            try { p = Process.GetProcessById(pid); }
            catch { return false; }
            try
            {
                if (p.HasExited) return false;
                long ft;
                if (!string.IsNullOrEmpty(procStart) && long.TryParse(procStart, out ft))
                {
                    long actual = p.StartTime.ToFileTimeUtc();
                    if (Math.Abs(actual - ft) > 50000000L) return false; // > 5 s apart: different process
                }
                return true;
            }
            catch { return true; }
            finally { p.Dispose(); }
        }
    }
}
