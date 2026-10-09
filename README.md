# KHost.Plugins.YouTube

YouTube media provider for [KHost](https://github.com/riddlemd/KHost). Adds a YouTube search provider to the console's
Song Search panel. **Enqueue** downloads the video into the library and queues it for the selected singer; its
sub-action **Open on YouTube** opens the video in a browser.

Searches run through [yt-dlp](https://github.com/yt-dlp/yt-dlp), so **there is no API key to
get and nothing to configure**.

Each result shows the video's own title. The artist is a best-effort parse of that title; the
channel (the uploader, not the performer) goes in `Notes` with the watch URL, and shows as
"Published by".

## Installing

From a host: Plugins, then Available, install YouTube Search, and restart KHost. It is one
portable build that runs on Windows, macOS and Linux.

By hand: unzip the release into its own folder under KHost's `plugins/` directory, enable it on
the Plugins page, and restart KHost. No key and no further setup; the plugin finds or fetches
yt-dlp itself.

yt-dlp merges separate video and audio streams with ffmpeg. This plugin does not locate, pass or
download ffmpeg, so yt-dlp uses whatever it finds for itself (usually on `PATH`). If none is
there, it falls back to a single pre-merged MP4 where YouTube offers one.

## Building

The contracts come from the `KHost.Abstractions` and `KHost.Common` packages (0.53.0). They are
not on nuget.org: build them into a local feed with KHost's `./build/pack-contracts.sh`, then
register it once with `dotnet nuget add source ~/.nuget/khost-local -n khost-local`.

```bash
dotnet build KHost.Plugins.YouTube.slnx
dotnet test tests/KHost.Plugins.YouTube.Tests
```

## How yt-dlp is found

Three tiers, in order:

1. **The `yt-dlp Path` setting**, if set. Wrong path is an error, not a reason to download a
   second copy behind your back.
2. **`yt-dlp` on `PATH`** — whatever the machine already has.
3. **A copy this plugin downloads**, into `cache/tools/` beside KHost, from yt-dlp's latest release,
   checked against its published SHA-512 before it runs. Windows (x64/arm64/x86), macOS (universal), and Linux (x64/arm64, glibc or musl) are all
   covered; 32-bit ARM Linux ships as a zip and is unpacked.

Tier 3 needs no action from the host. A downloaded copy is kept current by `yt-dlp -U` once per
run (the **Keep the downloaded yt-dlp up to date** setting, on by default); a copy from tier 1 or 2
is never updated by the plugin. See the next section before relying on tier 3.

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
