<img src="NudgeClock.png" width="96" align="right" alt="NudgeClock icon">

# NudgeClock

A terminal countdown for Windows that types **continue** into your running Claude Code sessions (the VS Code panels and the terminal ones) when it hits zero. It's handy when your agents stopped at a usage limit and you want them to pick the work back up when it resets, even if you're away.

- **One timer for every session, or one per session.** Pick a session with Tab and give it its own time.
- **Auto mode.** NudgeClock reads when each session's usage limit resets and continues that session by itself a minute later.
- **Nothing to install.** It's one small `.exe` that talks to Claude Code's local per-session inbox. Nothing goes over the network.

```text
 NUDGECLOCK   auto-continue for Claude Code                                                    Sat 02:37:19
────────────────────────────────────────────────────────────────────────────────────────────────────────────

                           ██████    ██        ██  ██  ██████      ██████    ██
                           ██  ██  ████    ██  ██  ██  ██  ██  ██  ██  ██  ████
                           ██  ██    ██        ██████  ██  ██      ██  ██    ██
                           ██  ██    ██    ██      ██  ██  ██  ██  ██  ██    ██
                           ██████  ██████          ██  ██████      ██████  ██████

  ● AUTO   api-server  ·  fires at 04:17:20  ·  in 1h 40m  ·  5-hour limit resets 04:16
  █████████████████████████████████████░░░░░░░░░░░░░░░░░░░   66%   PC kept awake  ·  3 more timers

 SESSIONS  4 of 4 in · Tab picks one                       msg "continue" · loop off · busy: send · auto on
  1  ●  api-server  idle     vscode   3h    auto 04:17:20 · 1h 40m · 5-hour limit
  2  ●  web-app     busy     vscode   1h    own timer 05:07:19 · 2h 30m
  3  ●  docs-site   idle     vscode   1h    main timer 05:37:19 · auto
  4  ●  mobile-app  idle     vscode   30m   auto Mon 09:38:18 · 2d 7h · weekly limit

 LOG
  02:37:19  ready · found 4 Claude sessions · type help for commands
  02:37:19  auto on · a session stopped by a usage limit continues 1m after the limit resets
  02:37:19  auto · api-server hit its 5-hour limit · resets 04:16 · continues at 04:17:20 (in 1h 40m)
  02:37:19  auto · mobile-app hit its weekly limit · resets Mon 09:37 · continues at Mon 09:38:18 (in 2d 7h)
  02:37:19  web-app · its own timer fires at 05:07:19 (in 2h 30m)
  02:37:19  armed · fires at 05:37:19 (in 3h 00m) · 1 session keeps its own timer

────────────────────────────────────────────────────────────────────────────────────────────────────────────
 › type a timer and press Enter
```

## Download

Get `NudgeClock.exe` from the [latest release](../../releases/latest), put it in any folder and double-click it. You can also [build it yourself](#build).

## Use it

Double-click `NudgeClock.exe`, type a timer and press Enter.

| You type | What happens |
|---|---|
| `25m` · `90s` · `1h30` · `2h15m` · `1:30:00` · `45` | countdown (a plain number means minutes) |
| `at 15:30` · `15:30` · `3pm` · `9:45am` | fires at that time of day (today, or tomorrow if it has already passed) |
| `+10m` · `-5m` | move the main timer |
| `stop` | cancel every timer |
| `now` | send right away |
| `auto` | turn auto mode on or off (see [Auto mode](#auto-mode)) |
| `msg keep going` | change the text that gets sent (`msg` on its own goes back to `continue`) |
| `loop` | start a timer over every time it fires |
| `busy` | toggle: skip sessions that are in the middle of a turn, or include them |
| `t 2` | leave session #2 out (`t 2` again puts it back) · `all` includes everyone |
| `help` · `clear` · `refresh` · `quit` | |

The session list refreshes on its own, so sessions you open or close appear and disappear by themselves. After sending, the log shows each session that got the message, then which ones actually started working.

### A different time for each session

Press **Tab** to pick a session (**Shift+Tab** goes the other way). The prompt shows which one you're talking to, and what you type next is for that session only:

| With a session picked | What happens |
|---|---|
| `25m` · `at 15:30` · … | gives it its own timer; the main timer leaves it alone |
| `+5m` · `-5m` | moves its timer (an auto timer you move becomes its own timer) |
| `stop` | removes its timer, so it follows the main timer again |
| `now` | sends to it right away |
| `auto` | turns auto mode on or off for this session only |
| `t` | leaves it out entirely |

**Esc** goes back to all sessions. You can also skip the picking and put the session's number first: `2 25m`, `2 at 15:30`, `2 +5m`, `2 stop`, `2 now`, `2 auto`.

### Auto mode

Type `auto`. When a session stops on a usage limit ("You've hit your 5-hour limit · resets 1:40am"), NudgeClock reads the exact reset time from that session's transcript and gives it a timer for one minute after it. A limit that resets in 1h39 means a 1h40 countdown. When the countdown ends, that session gets `continue`.

- Weekly limits work the same way, however far off the reset is.
- If the session is still on its limit afterwards, NudgeClock waits for the new reset time and tries again, at most once every 5 minutes.
- If you pick the session back up yourself first, its auto timer goes away on its own.
- If the limit had already reset when NudgeClock first saw it, the session continues 20 seconds later, which leaves time to `stop` it. A limit that reset more than 12 hours ago is left alone (`2 now` still sends).
- For each session, its own timer goes first, then auto, then the main timer.

Even with auto mode off, the session list shows which sessions are stopped on a limit and when it resets.

## Settings and options

`NudgeClock.ini`, next to the exe, keeps the message and the `loop`, `busy` and `auto` settings. `automargin=60` is how many seconds after the reset auto mode fires. Windows won't go to sleep while any timer is armed, auto timers included (the screen still can).

You can also start it with a timer already set: `NudgeClock.exe 2h`, `NudgeClock.exe at 3pm` or `NudgeClock.exe auto on`.
Other options: `--only <pid|name>,...` watches only the sessions you name. `--list` prints the sessions it can see and any usage limit they're stopped on. `--send [text]` sends once and exits.

## How it works

Each Claude Code session on this PC writes a small record to `%USERPROFILE%\.claude\sessions\` with its private message pipe and an access key. This is the same inbox that Claude sessions use to message each other. NudgeClock reads those records, checks that each process is still alive, and drops the message into each session's prompt queue. No windows are focused and no keys are faked, so you can keep working while it runs.

For auto mode, NudgeClock also reads the end of each session's transcript (`%USERPROFILE%\.claude\projects\<project>\<session id>.jsonl`). Claude Code records a usage-limit stop there, together with the exact time the limit resets. After the first look, NudgeClock only reads the lines added since.

## Things to know

- The session sees the text as a message **from another Claude session**, not as something you typed. In practice it just picks the work back up.
- A session in **bypass-permissions mode** holds messages like this for you to approve. Default, auto and accept-edits sessions get them right away.
- If a session is busy, the message lands in the turn it is already running. Use `busy` to skip those sessions.
- Without auto mode, set the timer a minute *after* the reset time.
- The pipe, key and transcript formats are internal to Claude Code. A future Claude Code update could change them. If sending or limit detection stops working, check those formats first.
- Windows only. NudgeClock is an independent tool, not made by or affiliated with Anthropic.

## Build

`src\build.bat` uses the .NET Framework 4 `csc.exe` that ships with Windows (C# 5), and draws the icon with `make-icon.ps1`. Nothing else is needed. The exe lands next to this README.

GitHub Actions builds every push. Pushing a tag such as `v1.1.0` also attaches `NudgeClock.exe` to a GitHub release.

## License

MIT, see [LICENSE](LICENSE).
