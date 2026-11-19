# Shipping the "Hide browsers" update

Everyone on 1.4.0 or newer picks this up by themselves. Installed copies check the GitHub releases of
`hamzamake/screen-time` (`Updater.Repo` in `ScreenTime\Updater.cs`). A new release with a higher version
and `ScreenTime-Setup.exe` attached is all an update needs. `HOW-TO-RELEASE.md` has the full guide.

## Steps

1. See which version is out right now:
   ```powershell
   gh release list --repo hamzamake/screen-time --limit 3
   ```
2. Pick the next version up. This is a small feature, so bump the middle or last number (for example
   1.4.0 → 1.4.1 or 1.5.0). It has to be higher than every release so far, betas included.
3. Build and publish in one go, from this folder:
   ```powershell
   powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.4.1 -Notes "All activity: new Hide browsers button, so you only see apps and websites"
   ```
   That builds `dist\ScreenTime-Setup.exe` stamped 1.4.1 and creates release `v1.4.1` with it attached.
   If you want to check it on GitHub before anyone gets it, add `-Draft` and click **Publish release** there
   when you're happy. Drafts are never offered as updates.
4. Check it went out:
   ```powershell
   gh release view v1.4.1 --repo hamzamake/screen-time
   ```
   `ScreenTime-Setup.exe` should be listed under assets.
5. Update your own PC: tray icon → **Check for updates** (or dashboard → Settings → **Check for updates now**).
   Afterwards the dashboard footer shows the new version, and **All activity** has the **Hide browsers** button.

Other people get it on their next check (every 6 hours by default). If they picked "only when I'm away", it
installs the next time they're idle or lock the PC.

## Pushing the code too (optional)

The release is what updates people. Pushing the source to the repo is separate and doesn't affect updates:
```powershell
git add -A
git commit -m "All activity: Hide browsers toggle"
git push
```
`build/`, `dist/`, `CLAUDE.md` and `HOW-TO-RELEASE.md` are already in `.gitignore`. `upd.md` isn't, so add
it there if you don't want it on GitHub.

## If it doesn't show up

- Version not higher than what they have → publish a higher one.
- Asset not named exactly `ScreenTime-Setup.exe`, still a draft, or marked pre-release → fix the release on
  GitHub.
- Repo private → it has to be public.
