# Third-party notices

RvtIO uses the following components.

| Component | Use | License |
|---|---|---|
| [OpenMcdf](https://github.com/ironfede/openmcdf) 3.4.0 | reads the compound file container of Revit files | MPL-2.0 |
| [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) 1.4.2 | stream decompression | MIT |
| [IOScene](https://github.com/IOExtend/IOScene) | scene contract used by RvtIO.Scene and RvtIO.Nwc | MIT, copyright (c) 2026 mahach666 |
| [NwcIO](https://github.com/IOExtend/NwcIO) 0.15.1 | NWC writer bundled with RvtIO.Nwc | MIT, copyright (c) 2026 mahach666 |
| .NET runtime libraries (DeflateManaged sources, `src/RvtIO/DeflateManaged`) | managed inflate implementation adapted for tolerant decoding | MIT, copyright (c) .NET Foundation and Contributors |

Development and build-time packages (xunit, Microsoft.NET.Test.Sdk, Microsoft.SourceLink.GitHub) are not part of the
published packages.

Autodesk and Revit are trademarks of Autodesk, Inc. RvtIO is not affiliated with, endorsed by or sponsored by Autodesk.
