# HEIC to JPEG

A Windows 11 Explorer context menu entry that converts selected `.heic` / `.heif`
images to `.jpg`.

Select any number of HEIC files, right-click, choose **Convert to JPEG**. The
entry appears in the top level of the Windows 11 menu, not under *Show more
options*.

## How it works

| Piece | What it does |
| --- | --- |
| `src/ShellExt` | `IExplorerCommand` COM server (C++). Reads the selection, launches the converter, returns immediately. |
| `src/Heic2Jpg` | Console app (.NET 10) that decodes HEIC and encodes JPEG. |
| `package/` | Sparse MSIX manifest that gives the extension a package identity. |

Decoding uses the Windows Imaging Component — the same decoder Explorer already
uses for HEIC thumbnails. There is no bundled codec and no ImageMagick
dependency. Capture metadata (date taken, camera, exposure, GPS) is carried over
to the JPEG.

The shell extension itself never touches pixels. It runs inside Explorer's COM
surrogate, so it only collects file paths and starts a separate process; slow or
failing conversions can't stall the shell.

## Requirements

- Windows 11 22H2 (build 22621) or newer
- [HEIF Image Extensions](https://apps.microsoft.com/detail/9pmmsr1cgpwg) from the Microsoft Store
- [.NET Desktop Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0) — or build with `-SelfContained` to bundle it

To build: Visual Studio Build Tools with the C++ workload, the Windows 11 SDK,
and the .NET 10 SDK.

## Install

```powershell
.\scripts\build.ps1

# Elevated: trusts a self-signed certificate so Windows will accept the package.
.\scripts\install.ps1
```

`install.ps1` creates a self-signed code-signing certificate, adds it to
`LocalMachine\TrustedPeople`, signs the package, and registers it. Windows will
not load a packaged shell extension whose signature it cannot validate, so this
step is unavoidable without a purchased certificate.

Uninstall with `.\scripts\uninstall.ps1`, which also removes the certificate.

## Usage from the command line

```powershell
.\dist\heic2jpg.exe [--quality 1-100] [--outdir DIR] <file.heic> [more files...]
```

Quality defaults to 92. Conversions are logged to
`%LOCALAPPDATA%\heic-to-jpg\convert.log`; failures also raise a dialog, since
there is no console when launched from Explorer.

## Notes

The package is *sparse* — the `.msix` contains only the manifest and logos, and
points at `dist\` for the binaries. Rebuilding the DLL or the converter does not
require repackaging or reinstalling.
