# KHost.Plugin.YouTube

YouTube media provider for [KHost](../KHost). Adds a YouTube Data API v3 search provider
to the console's Song Search panel, with an "Open on YouTube" result action.

Results carry the video title as `Title` and leave `Artist` empty — a video title is one
string, and the channel is the uploader rather than the performer, so it stays in `Notes`.

## Building

Requires a sibling checkout of the KHost repo (the plugin compiles against
`KHost.Plugins.Sdk` by project reference until the Sdk ships as a NuGet package):

```
~/Developer/riddlemd/
  KHost/
  KHost.Plugin.YouTube/
```

```bash
dotnet build KHost.Plugin.YouTube.slnx
dotnet test tests/KHost.Plugin.YouTube.Tests
```

Building also drops the plugin into the sibling KHost checkout's runtime plugins folder
(`src/KHost.UserInterface/bin/Debug/net10.0/plugins/khost.youtube/`) when it exists.

## Installing

Copy the build output (entry dll, `manifest.json`, and dependency dlls) into a folder under
KHost's `plugins/` directory, enable it on KHost's Plugins settings page, and restart KHost.
Set a YouTube Data API key in the plugin's settings to get results.
