# Third-party notices

Cloudlet invokes the existing rclone executable as a separate process. The optional WinFsp installer is distributed unchanged; it is not run as part of building or packaging this application. Copyright and license ownership remain with the respective projects.

## rclone 1.75.1

- Copyright (C) 2012 by Nick Craig-Wood.
- License: MIT.
- Bundled file: `rclone.exe`.
- License copy: `docs/licenses/rclone-1.75.1-COPYING.txt`.
- [Official license page](https://rclone.org/licence/).
- [Version 1.75.1 license](https://github.com/rclone/rclone/blob/v1.75.1/COPYING).
- [Version 1.75.1 source](https://github.com/rclone/rclone/tree/v1.75.1).

The pinned release's COPYING file is the authoritative copyright and license text for this bundled version. The current website may show a different copyright year.

## WinFsp 2.1.25156

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos

- Bundled file: `winfsp-2.1.25156.msi` (the existing official installer).
- License: GPL version 3 with the upstream FLOSS exception; the complete terms are preserved in `docs/licenses/WinFsp-2.1-License.txt`.
- [WinFsp repository](https://github.com/winfsp/winfsp).
- [Version 2.1 license](https://github.com/winfsp/winfsp/blob/v2.1/License.txt).
- [Version 2.1 source and releases](https://github.com/winfsp/winfsp/releases/tag/v2.1).
- [Official download and installation documentation](https://winfsp.dev/rel/).

WinFsp is optional until the user needs filesystem mounting. Its installer requires the user's explicit selection and may request administrator permission. Cloudlet's installer does not silently install, upgrade, or remove WinFsp.

## Microsoft .NET 10 and Windows App SDK / WinUI 3

The self-contained publish output includes Microsoft runtime and native dependency files. Their respective license notices remain applicable; generated dependency and runtime metadata are retained in the package. The build copies the selected SDK's `LICENSE.txt` and `ThirdPartyNotices.txt` into `docs/licenses/dotnet-*` when supplied by that SDK. Top-level license and notice files from restored NuGet packages are copied into `docs/licenses/packages`; `docs/licenses/restored-packages.json` identifies exact package versions and copied notices.

- [.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) and [third-party notices](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT).
- [Windows App SDK license](https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE).
- [WinUI license](https://github.com/microsoft/microsoft-ui-xaml/blob/main/LICENSE).

The exact restored dependency versions are recorded in project package references and build output metadata. The Windows SDK and Inno Setup compiler are build tools and are not copied wholesale into the application package.
