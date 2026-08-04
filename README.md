# TrampList Manager

A small Windows companion app for [TrampList](https://www.tramplist.pro) — installs Trampler
designs into *SAND: Raiders of Sophie*, and helps you share your own.

## What it does

**Install a design** — three ways, all ending in the same place:

| | |
|---|---|
| Paste an ID or link | `test-8bed65`, a `tramplist.pro/d/…` URL, or a `tramplist://` link |
| Double-click a `.wbt` | after registering the file association |
| Drag and drop | drop a `.wbt` anywhere on the window |

The file is copied into your Walkers folder under a **freshly generated UUID**. That rename is
the fiddly step when doing it by hand, and it is why installing the same design twice gives you two
independent copies rather than a collision — the game keys a design's identity off its filename.

**Share your own** — lists the Tramplers in your Walkers folder with size and date. "Share this
design" opens the upload page and reveals the file in Explorer, ready to drag into the form.

The app holds no account and uploads nothing itself.

## Safety

- Offers to **back up your Walkers folder** before the first install of a session.
- Refuses anything that is not a plausible blueprint: gzip magic (`1f 8b 08`) and a sane size.
- Installs to a temporary file first when downloading, so a failed transfer never leaves a
  half-written file where the game will read it.
- Never overwrites an existing design — a UUID collision would mean destroying someone's build.
- Registry changes are per-user (`HKCU`), so no administrator rights and nothing machine-wide.

It touches only your own save folder. It does not read, write or attach to the game process —
SAND runs BattlEye, and this app stays well clear of it.

## Building

```sh
dotnet build src/TrampListManager
dotnet run   --project src/TrampListManager
```

Requires the .NET 10 SDK. Windows only: WPF does not run on Linux or macOS. The `Services/`
layer is free of Windows APIs apart from `ShellIntegration`, so an Avalonia front-end could be
added later without rewriting the logic.

### Verification

```sh
dotnet run --project tests/Verify
```

Exercises slug parsing, folder listing and install round-trips against a **real SAND install**,
including that installed bytes are unchanged and that malformed input is rejected. It installs only
into a temporary folder — your real Tramplers are never touched.

## Publishing a single .exe

```sh
dotnet publish src/TrampListManager -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Notes

Not affiliated with Hologryph or tinyBuild.
