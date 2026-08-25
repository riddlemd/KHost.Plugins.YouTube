# KHost.Plugins.YouTube

YouTube media provider for [KHost](../KHost). Adds a YouTube search provider to the console's
Song Search panel, with an "Open on YouTube" result action.

Searches run through [yt-dlp](https://github.com/yt-dlp/yt-dlp), so **there is no API key to
get and nothing to configure**. The YouTube Data API was dropped for it: a key needs a Google
Cloud project, and its free quota is 10,000 units a day against 100 units per search — about
100 searches, which one karaoke night spends.

Results carry the video title as `Title` and leave `Artist` empty — a video title is one
string, and the channel is the uploader rather than the performer, so it stays in `Notes`.

## Building

Requires a sibling checkout of the KHost repo (the plugin compiles against
`KHost.Plugins.Sdk` by project reference until the Sdk ships as a NuGet package):

```
~/Developer/riddlemd/
  KHost/
  KHost.Plugins.YouTube/
```

```bash
dotnet build KHost.Plugins.YouTube.slnx
dotnet test tests/KHost.Plugins.YouTube.Tests
```

Building also drops the plugin into the sibling KHost checkout's runtime plugins folder
(`src/KHost.UserInterface/bin/Debug/net10.0/plugins/khost.youtube/`) when it exists.

## Installing

Copy the build output (entry dll, `manifest.json`, and dependency dlls) into a folder under
KHost's `plugins/` directory, enable it on KHost's Plugins settings page, and restart KHost.
No key, and no further setup — the first search finds yt-dlp or fetches it.

## How yt-dlp is found

Three tiers, in order:

1. **The `yt-dlp Path` setting**, if set. Wrong path is an error, not a reason to download a
   second copy behind your back.
2. **`yt-dlp` on `PATH`** — whatever the machine already has.
3. **A copy this plugin downloads**, into KHost's `cache/tools/`, from yt-dlp's latest release.
   Windows (x64/arm64/x86), macOS (universal), and Linux (x64/arm64, glibc or musl) are all
   covered; 32-bit ARM Linux ships as a zip and is unpacked.

Tier 3 needs no action from the host, which is the point. But see the next section before
relying on it.

## macOS: yt-dlp's own build is slow here — install it instead

**Symptom:** searches take several seconds to well over twenty on macOS, and it is not the
network.

**Cause:** yt-dlp's macOS build is a ~37MB unsigned single-file PyInstaller bundle. macOS
rescans binaries like that on launch, and yt-dlp is a one-shot command, so the cost is paid on
every search. `yt-dlp --version`, which makes no request at all, is just as slow as a search.

**Fix: install yt-dlp yourself.** A package-manager install is a small Python entry script
against a normal interpreter — no single-file bundle, nothing to rescan. The plugin finds it on
`PATH` (tier 2) and never downloads anything.

```bash
brew install yt-dlp                 # macOS
winget install yt-dlp.yt-dlp        # Windows
sudo apt install yt-dlp             # Debian/Ubuntu — or: pipx install yt-dlp
```

Measured on an Apple silicon Mac, same yt-dlp version (2026.08.19), same machine:

| | Downloaded bundle | `brew install yt-dlp` |
|---|---|---|
| `--version` (no network) | 7.7s warm, 20.8-26.8s cold | **0.12-0.14s** |
| Search, 10 results | 9.0s warm, 16.9-20.4s cold | **1.08-1.16s** |
| Through the plugin's own resolver | 22.7s | **1.16s** |

The bundle's cost varies because the scan result is cached for a while and re-earned later — the
cold numbers are what a host meets on a fresh session, which is the number that matters at the
start of a night. The brew install was steady across every run.

What it is not: ad-hoc `codesign` made no difference (20.9/22.1/21.7s), nor did forcing the arm64
slice (22.1s; x86_64 under Rosetta was worse at 36.4s), nor `TMPDIR` (20.7s), nor where the file
lives (23.8-26.8s across three locations). Reading the same 37MB off disk takes 0.00s, so it is
not I/O, and no third-party antivirus was installed.

After installing, either leave the `yt-dlp Path` setting blank and let `PATH` find it, or set it
to the output of `which yt-dlp`.

Windows and Linux were not measured. The scan described here is macOS-specific, but Windows
Defender scans executables too, so the same "install it rather than let the plugin download it"
advice is the safer default everywhere.

## Keeping yt-dlp current

YouTube changes break extraction periodically, and yt-dlp ships fixes fast — 19 stable releases
in the last year plus nightly builds. A host that starts failing usually just needs a newer
yt-dlp:

```bash
yt-dlp -U                    # a standalone binary updates itself
brew upgrade yt-dlp          # or whatever installed it
```

This is why the binary is fetched rather than bundled into the plugin: bundling would mean a
plugin rebuild and re-release for every YouTube change.
