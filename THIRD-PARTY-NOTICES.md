# Third-Party Notices

Skadi includes third-party software that remains subject to its own license terms. This file summarizes the dependencies distributed with the Windows application. It does not replace the original license texts or grant additional rights.

## Native Runtime

| Component | Version | License | Project |
|---|---:|---|---|
| Intel oneVPL dispatcher | pinned runtime | MIT | [uxlfoundation/oneVPL](https://github.com/uxlfoundation/oneVPL) |

The complete oneVPL MIT license text is distributed at `ThirdParty/oneVPL/LICENSE.txt`.

## NuGet Dependencies

| Component | Version | License | Project |
|---|---:|---|---|
| LibreHardwareMonitorLib | 0.9.7-pre675 | MPL-2.0 | [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) |
| BlackSharp.Core | 1.0.12 | MPL-2.0 | [BlackSharp](https://github.com/Blacktempel/BlackSharp) |
| DiskInfoToolkit | 2.0.3 | MPL-2.0 | [DiskInfoToolkit](https://github.com/Blacktempel/DiskInfoToolkit) |
| RAMSPDToolkit-NDD | 1.5.0 | MPL-2.0 | [RAMSPDToolkit](https://github.com/Blacktempel/RAMSPDToolkit) |
| NAudio and NAudio component packages | 2.3.0 | MIT | [NAudio](https://github.com/naudio/NAudio) |
| SharpDX, SharpDX.Direct3D11, SharpDX.DXGI | 4.2.0 | MIT | [SharpDX](https://github.com/sharpdx/SharpDX) |
| HidSharp | 2.6.4 | package license | [HidSharp](https://www.nuget.org/packages/HidSharp/2.6.4) |
| Mono.Posix.NETStandard | 1.0.0 | package license | [Mono.Posix.NETStandard](https://www.nuget.org/packages/Mono.Posix.NETStandard/1.0.0) |
| Microsoft .NET support packages | resolved by NuGet | MIT or applicable Microsoft terms | [.NET](https://github.com/dotnet/runtime) |

The package graph also contains framework and runtime-specific transitive assets selected by NuGet for the target platform. Exact resolved versions are recorded in `EventCapture.Core/obj/project.assets.json` after restore and in the published `Skadi.deps.json`.

## License References

- Mozilla Public License 2.0: <https://www.mozilla.org/MPL/2.0/>
- MIT License: <https://opensource.org/license/mit>
- NuGet package metadata: <https://www.nuget.org/>

Copyright remains with each component's respective authors and contributors. Refer to the linked projects and package metadata for the complete notices and license texts applicable to each distributed version.
