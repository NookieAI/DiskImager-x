# DiskImager (cross-platform)

Fast, safe disk imaging for **Windows, macOS, and Linux** — one app, native single-file
binary per OS. Back up a USB stick or SD card to a file, write an image back, verify it,
or format a drive to FAT32.

> This is the cross-platform rewrite (.NET 7 + [Avalonia](https://avaloniaui.net/)) of the
> Windows-only [DiskImager](https://github.com/NookieAI/DiskImager). Same engine, portable UI.

## Download

Grab the standalone binary for your OS from the [Releases](../../releases) page — no installer,
no runtime to install:

| OS | File | Run as |
|----|------|--------|
| Windows | `DiskImager-windows-x64.exe` | right-click → **Run as administrator** |
| macOS (Apple Silicon) | `DiskImager-macos-arm64` | `chmod +x` then `sudo ./DiskImager-macos-arm64` |
| macOS (Intel) | `DiskImager-macos-x64` | `chmod +x` then `sudo ./…` |
| Linux | `DiskImager-linux-x64` | `chmod +x` then `sudo ./DiskImager-linux-x64` |

Raw disk access needs elevation on every OS (admin / root).

## Modes

- **Backup** — disk → image (`.img`, optional gzip `.img.gz`, optional SHA-256 sidecar)
- **Restore** — image → disk (raw · gzip · zip · vhd), with smart-restore (skip zero regions) and verify-after-write
- **Verify** — compare a disk against an image, byte-for-byte
- **Format** — create a fresh FAT32 volume
- **Mount** — open an image as a drive (`hdiutil` / `udisksctl` / `Mount-DiskImage`)

## Safety

The disk that runs your OS is detected, tagged **[SYSTEM]**, and can't be erased without typing
**ERASE**. Every destructive action names the exact device first.

## Build from source

Requires the [.NET 7 SDK](https://dotnet.microsoft.com/download).

```bash
cd src/DiskImager.App
dotnet run                 # launch the app
dotnet run -- --list       # print detected disks (read-only)
dotnet run -- --selftest   # run the engine unit tests

# standalone single-file build for your OS:
dotnet publish -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```

Pushing a `v*` tag builds all four binaries via GitHub Actions and attaches them to the release.
