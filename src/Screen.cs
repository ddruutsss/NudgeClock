using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace NudgeClock
{
    struct Rgb
    {
        public readonly byte R, G, B;
        public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }
        public bool Same(Rgb o) { return R == o.R && G == o.G && B == o.B; }
        public string Fg { get { return Vt.ESC + "[38;2;" + R + ";" + G + ";" + B + "m"; } }
    }

    // Amber-on-dark palette, used on the terminal's own background.
    static class C
    {
        public static readonly Rgb Amber = new Rgb(255, 176, 0);
        public static readonly Rgb Ember = new Rgb(255, 112, 52);
        public static readonly Rgb Unlit = new Rgb(52, 42, 28);
        public static readonly Rgb Text = new Rgb(224, 218, 204);
        public static readonly Rgb Muted = new Rgb(134, 127, 114);
        public static readonly Rgb Dim = new Rgb(78, 73, 66);
        public static readonly Rgb Green = new Rgb(126, 212, 145);
        public static readonly Rgb Red = new Rgb(255, 110, 110);
        public static readonly Rgb Cyan = new Rgb(110, 196, 217);
        public static readonly Rgb Warn = new Rgb(240, 200, 96);
    }

    static class Vt
    {
        public const string ESC = "\u001b";

        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
        [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint mode);
        [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint mode);
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

        public static bool Enable()
        {
            IntPtr h = GetStdHandle(-11);
            uint mode;
            if (!GetConsoleMode(h, out mode)) return false;
            return SetConsoleMode(h, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
        }

        // Keeps Windows from sleeping while a timer is armed (the screen may still turn off).
        public static void KeepAwake(bool on)
        {
            try { SetThreadExecutionState(on ? 0x80000001u : 0x80000000u); } catch { }
        }
    }

    // One row of the screen: colored text clipped to a fixed width.
    class Line
    {
        readonly StringBuilder ansi = new StringBuilder();
        readonly StringBuilder plain = new StringBuilder();
        readonly int width;
        bool hasColor;
        Rgb color;
        public int Len { get { return plain.Length; } }

        public Line(int width) { this.width = Math.Max(0, width); }

        public Line Add(string text, Rgb c)
        {
            if (string.IsNullOrEmpty(text)) return this;
            int room = width - Len;
            if (room <= 0) return this;
            if (text.Length > room) text = text.Substring(0, room);
            if (!hasColor || !color.Same(c)) { ansi.Append(c.Fg); color = c; hasColor = true; }
            ansi.Append(text);
            plain.Append(text);
            return this;
        }

        public Line Bold(string text, Rgb c)
        {
            ansi.Append(Vt.ESC + "[1m");
            Add(text, c);
            ansi.Append(Vt.ESC + "[22m");
            return this;
        }

        // Drawn on screen, but blank in the plain-text copy (the unlit cells of the big clock).
        public Line Ghost(string text, Rgb c)
        {
            int before = Len;
            Add(text, c);
            int added = Len - before;
            if (added > 0) { plain.Length = before; plain.Append(' ', added); }
            return this;
        }

        public Line Gap(int n) { return n > 0 ? Add(new string(' ', n), C.Text) : this; }

        public Line To(int col) { return Gap(col - Len); }

        // Places text flush against the right edge (if there's room left for it).
        public Line Right(string text, Rgb c)
        {
            if (Len + text.Length + 1 > width) return this;
            To(width - text.Length);
            return Add(text, c);
        }

        public string Ansi { get { return ansi.ToString() + Vt.ESC + "[0m"; } }
        public string Plain { get { return plain.ToString(); } }
    }

    // Writes only the rows that changed since the last frame, so the screen never flickers.
    class Screen
    {
        string[] prev;
        int prevW = -1, prevH = -1;

        public void Present(List<Line> lines, int w, int h)
        {
            bool full = prev == null || w != prevW || h != prevH;
            var sb = new StringBuilder();
            if (full) sb.Append(Vt.ESC + "[0m" + Vt.ESC + "[2J");
            var now = new string[h];
            for (int i = 0; i < h; i++)
            {
                string a = i < lines.Count ? lines[i].Ansi : "";
                now[i] = a;
                if (!full && prev[i] == a) continue;
                sb.Append(Vt.ESC + "[").Append(i + 1).Append(";1H").Append(a).Append(Vt.ESC + "[K");
            }
            if (sb.Length > 0) { Console.Out.Write(sb.ToString()); Console.Out.Flush(); }
            prev = now; prevW = w; prevH = h;
        }

        public void Invalidate() { prev = null; }
    }
}
