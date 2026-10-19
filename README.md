# Screen Time

Tracks how long you use every app on your PC, and every website (with the browser extension).
Keeps all history forever.

## Install (for anyone)

Give people **`dist\ScreenTime-Setup.exe`** (about 40 MB). It works on any Windows 10/11 PC with nothing else
to install first, and needs no administrator rights (it installs for the current Windows account only).

1. Run `ScreenTime-Setup.exe` and click **Install**. Options: start with Windows, desktop shortcut.
   It installs to `%LOCALAPPDATA%\Programs\ScreenTime` and starts Screen Time (clock icon in the tray).
2. Click **Continue** and the **website tracking wizard** opens. It detects the installed browsers (default
   browser first) and walks through adding the extension, with steps and pictures for that browser:
   1. open the extensions page (a button does it)
   2. turn on Developer mode (a checkbox, and Next unlocks after a few seconds)
   3. click Load unpacked and pick the folder (the path is already copied to the clipboard). **Finish only
      unlocks once the extension really connects to the app**, so the steps can't be skipped through by accident.

   "I'll do this later" asks for confirmation first. The wizard can be reopened any time from the tray menu:
   **Set up browser extension…**

Running the setup again updates Screen Time in place and keeps all history (and your shortcut / start with
Windows choices). After that, Screen Time **updates itself** from GitHub releases (see below). Uninstall from Windows
Settings → Apps (or run `ScreenTime.exe --uninstall`); it asks whether to keep the history (it keeps it by default).

### Automatic extension install (optional)

Browsers don't let programs silently install extensions that aren't on their official stores (an anti-malware
rule), so out of the box the wizard uses the guided steps. To make it automatic:

1. Publish `dist\extension-for-store.zip` on the Chrome Web Store (one-time $5 developer fee) and/or Microsoft
   Edge Add-ons (free).
2. Put the extension ids into `ExtensionStore.ChromeWebStoreId` / `EdgeAddonsId` in `ScreenTime\Browsers.cs`
   and rebuild.

The wizard then registers the extension through the browsers' official "external extensions" registry keys
(Chrome, Brave, Edge). The browser shows "New extension added" and the user just clicks **Enable**.
Other browsers keep using the guided steps.

## Updates

Every installed Screen Time checks the latest GitHub release of the repo in `Updater.Repo`
(`ScreenTime\Updater.cs`, currently `hamzamake/screen-time`) 2 minutes after it starts and then every 6 hours.
If the release tag (`v1.5.0`) is a higher version and the release has `ScreenTime-Setup.exe` attached, it:

1. downloads the setup to `%TEMP%\ScreenTime-Update` and checks it (size, GitHub's SHA-256 digest, and that it
   really is the Screen Time setup for that version),
2. runs it with `--update`: silent, same install folder, keeps the user's shortcuts and start-with-Windows choice,
3. the setup closes Screen Time (it saves first), installs, and starts the new version. A notification says
   "Screen Time was updated", and an open dashboard reloads itself. If the install fails, the old version starts
   again and says why.

**Tracking doesn't stop during an update.** The download happens in the background while tracking goes on.
For the restart itself (a few seconds), the old version hands over what it was counting (the app in front, the
browser profile and site, open apps and tabs), and the new version counts those seconds for the same things,
with the usual idle rule (if you're away, they're dropped). It also knows your browser tabs immediately instead
of waiting for the extension's next check-in. Only an update restart does this; quitting with **Exit** stops
tracking as before.

Users can turn off **Settings → Install updates automatically**. Then they get a notification and a banner in
the dashboard (**What's new** / **Update now** / **Later**) instead. **Check for updates** is in the tray menu, in
Settings and at the bottom of the dashboard. Drafts and pre-releases are never offered.

The browser extension's files are replaced too. An unpacked ("Load unpacked") extension reloads itself once it
sees the app's new version; a store install updates through its store.

### Publishing an update

One-time: create the GitHub repo named in `Updater.Repo` as a **public** repo with at least one commit (tick
"Add a README"). If you use a different name, change `Updater.Repo` and rebuild.

Then, for each update:

```powershell
powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.5.0 -Notes "What changed"
```

It builds `dist\ScreenTime-Setup.exe` for that version and creates release `v1.5.0` with the setup attached
(`-NotesFile notes.md` for longer notes, `-Draft` to review it on GitHub first). The version must be higher
than the last release. Doing it by hand works too: `build.ps1 -Version 1.5.0`, then create a release tagged
`v1.5.0` on GitHub and attach `dist\ScreenTime-Setup.exe` with exactly that name.

Copies older than 1.4.0 don't have the updater yet, so give those people the 1.4.0 setup once.

## Use it (from source)

Run `powershell -ExecutionPolicy Bypass -File build.ps1`, then run `dist\ScreenTime-Setup.exe`.
Running `ScreenTime.exe` again while it's already running just opens the dashboard.
## Dashboard

- Ranges: today, yesterday, 7/30 days, this year, all time, custom. Arrows (or `←` `→`) step through periods.
- Total time compared with the previous period, busiest hour, daily average, top app and site.
- Chart by hour/day/week/month (with a table view), and a **weekly pattern** heatmap for ranges of 7+ days.
- **Usage time vs open time**: usage = the app/site was in front and you were active; open = an app had any window open (minimized counts, tray-only doesn't) or a website had any tab open. Lists show both (light bar = open, solid = used) and can be sorted by either; a selected app/site's chart shows open time behind usage. Open time ignores idle but never counts while paused, locked or asleep. Turn it off in **Settings** (gear button) or the tray menu.
- **All activity** chart: apps and websites in one ranking. Browser time is split into its websites plus "other pages", so nothing is counted twice and the rows add up to your total.
- **Settings** (gear button): track open time, keep counting while sound plays, start with Windows, idle timeout.
- Click an app or website to chart just that one. The **⋯** menu next to each one can:
  - **Rename** it (display only)
  - **Set a daily limit** (Windows notification when you pass it, plus a heads-up 5 minutes before for limits
    of 15 minutes or more)
  - **Merge** it into another (when one app shows up twice); separate again any time
  - **Hide** it from the stats (data is kept; tick "Show hidden" at the bottom to see it again)
- **Daily limits** card: progress for today; add limits for an app, a website or total screen time.
- **Export all data (CSV)** and **Import from ActivityWatch** at the bottom.
- **Pause button** (⏸ next to the gear): pause for 15 min / 1 hour / 3 hours / until tomorrow / until you
  resume. It turns into ▶ to resume, and the status pill shows until when.
- Light/dark theme button; keyboard shortcuts `T` `Y` `W` `M` `A`, `←` `→`, `/` search apps, `P` pause/resume,
  `Esc` clears the selection.
- Version number and **Check for updates** at the bottom; a banner when an update is waiting.

## Browser trackers (Work, Personal, ...)

Every browser installation or **profile** with the extension is its own "browser" in Screen Time (two
Vivaldi profiles = two browsers). Group them into trackers like **Work** and **Personal**:

- **Dashboard → Manage trackers** (or Settings → Manage browser trackers…): create, rename and delete groups,
  assign each browser to a group, rename browsers, and choose what counts toward **All Time** (per group, for
  Unassigned, and per browser). Excluded groups/browsers still track their own time.
- Or, in each browser: click the Screen Time extension icon. It shows that browser's name and lets you pick
  its group (handy for telling identical Vivaldi setups apart).
- The **Browser trackers** cards show All Time, each group and Unassigned for the selected period, with
  all-time totals and top sites.
- New browsers start in **Unassigned**, never in a group. Deleting a group moves its browsers to Unassigned or
  to a group you pick; their recorded time stays with them.
- No double counting: each second of browsing goes to exactly one browser, the profile whose window is in front
  (the extension reports focus).
- Groups and settings live in the desktop app, so every browser sees the same configuration automatically.
  Nothing has to be set up per browser except (optionally) its group. All times use the PC's clock.
- Browsing recorded before this feature (and ActivityWatch imports) can't be split by profile; it appears as
  **Earlier browsing (before trackers)**, which you can assign to a group or exclude.
## Tray menu

- Today's total and top 5 apps (right in the menu)
- **Pause tracking** for 15 min / 1 hour / 3 hours / until tomorrow / until you resume
- **Idle timeout**: after this long without mouse/keyboard input you count as away (default 5 min).
  The time you were away is removed completely, not just the time after the timeout.
- **Keep counting while app plays sound**: watching a video or sitting on a call without touching
  the mouse still counts, as long as the app in front is actually playing sound.
- **Start with Windows**, **Open data folder**, **Check for updates** (shows the version; becomes
  **Install update x.y.z** when one is waiting), **Exit**

## How apps are told apart

An app is identified by where it's installed, with version numbers ignored. So:
- Discord, which moves to a new `app-1.0.xxxx` folder on every update, stays **one** app.
- Two different programs that are both called e.g. `Launcher.exe` stay **separate**, and the dashboard
  shows where each one is from ("Launcher · Epic Games" / "Launcher · Riot Games").
- If something still shows up twice, use **⋯ → Merge**.

## Importing from ActivityWatch

1. In ActivityWatch, open **Raw Data** and **export** these buckets (JSON or CSV) into your Downloads folder:
   `aw-watcher-window` (apps), `aw-watcher-afk` (so time away isn't counted) and `aw-watcher-web-…` (websites, optional).
2. In the dashboard, click **Import from ActivityWatch**, keep all files ticked, and click **Import**.

The import only adds to your data. Time ranges Screen Time already recorded, or that were imported
before, are skipped, so importing the same files again (or a newer export that overlaps) never
overwrites anything or counts anything twice. App time only counts while ActivityWatch says you
weren't AFK, and website time only while the browser was the active window.

## Where the data is

`%LOCALAPPDATA%\ScreenTime\screentime.db` (SQLite). Nothing is ever deleted. It's stored as hourly
totals per app and per website, so it stays small (a few MB per year).

Automatic backups in `%LOCALAPPDATA%\ScreenTime\backups`: a daily snapshot (newest 7 kept) and one
snapshot per month, kept forever.

## Performance

- One timer tick per second: checks the foreground window and last-input time (microseconds of work).
  About 0.1% of one CPU core while you use the PC; ~14 MB of memory.
- Time is collected in memory and written to disk once a minute in one transaction (and on exit,
  sleep and shutdown).
- The "is it playing sound" check only runs while you're idle (every 30 s, or every 10 s past the timeout).
- The extension doesn't time anything itself. It tells the app which domain the active tab is on
  when it changes, plus a 30 s heartbeat. The app only counts a site while the browser is the window
  in front, so app time and website time always agree.
- Uses the SQLite engine built into Windows (`winsqlite3.dll`), so there are no extra dependencies.
- The local server only listens on 127.0.0.1. Changes are only accepted from the dashboard page itself.

## Build

Needs the .NET 9 SDK: `powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.4.0`. It publishes the app self-contained, packs it into `dist\ScreenTime-Setup.exe` (the installer targets .NET Framework 4.8, built into Windows) and writes `dist\extension-for-store.zip`. Source: `ScreenTime\` (app), `installer\` (setup), `extension\` (browser extension). The setup also takes
`/S` (silent), `--update` (silent, keeps the current install's choices; used by the updater), `--dir=`,
`--no-start`, `--no-shortcuts`, `--no-autostart` and `--no-register`.

## Note for AI assistants

**Never push anything to GitHub, never commit, and never do anything in the repo** (no git commands, no
`gh repo`/`gh release`). Only edit and build files here. The owner publishes releases. See `CLAUDE.md`.
