# RVT to NWC

## Autonomous export

Download and extract `RvtIO-Nwc-<version>-net10.0.zip` or `RvtIO-Nwc-<version>-net8.0.zip` from the
[RvtIO releases](https://github.com/IOExtend/RvtIO/releases/latest). The archive includes the tool and its dependencies.
Install the .NET runtime that matches the archive and run the command from the extracted directory.

```sh
dotnet RvtIO.Nwc.dll model.rvt model.nwc --native
```

Native export writes placed triangles, straight and circular model lines, separation lines, reinforcement meshes
and area reinforcement outlines. It preserves the selection tree, including stair components, along with face
normals, diffuse colours, IFC GlobalIds and Revit property display text. Exported grid lines and level outlines
are hidden when the file opens. The cache uses feet internally and opens in NwcIO and Navisworks Manage 2027.
Export uses managed .NET code on Windows and Linux.

## Autodesk Navisworks

On Windows, pass the installed Navisworks Manage or Simulate executable to export a cache for Navisworks.

```powershell
dotnet RvtIO.Nwc.dll model.rvt model.nwc --navisworks "C:\Program Files\Autodesk\Navisworks Manage 2027\Roamer.exe"
```

RvtIO reads the RVT and exports a temporary IFC using its current IFC profile and Revit property sets.
Navisworks creates the NWC and opens it before the tool moves it to the requested path.
The cache's geometry and selection tree follow Navisworks' IFC import. The command requires a working
Navisworks installation. Tested with Navisworks Manage 2027.
The tested 2027 cache uses an IFC property record layout that NwcIO's managed reader does not yet support.

Autodesk documents the [command-line switches](https://help.autodesk.com/cloudhelp/2022/ENU/Navisworks/files/GUID-3891FB9F-7BF1-4510-A1EE-121E975C0ACE.htm) used to create and open the cache.

## NwcIO preview

Run the tool with two positional arguments to write the NwcIO preview layout.

```sh
dotnet RvtIO.Nwc.dll model.rvt model.nwc
nwcio model.nwc model.json
```

The tool writes placed triangle meshes in metres in the internal model coordinates, Z up, with one
mesh per scene instance. It applies rotation,
scale and reflection to the local vertices, corrects reflected triangle winding, and keeps the translation
separate from the float32 vertex buffer. The cache includes the element tree and available storeys, Revit
element IDs and UniqueIds, IFC GlobalIds, and instance and type parameter display text. Mesh names include
the Revit element ID so that meshes can be matched to the element tree.

The cache marks its coordinates as metres and records mesh ownership in the writer metadata. A build of
NwcIO with linked-cache support can open it and export the placed geometry and property tabs with
`nwcio model.nwc --scene model.scene.json`. NwcIO 0.14 can inspect the mesh summaries and element tree via
its JSON export. This preview layout is for NwcIO. The cache layout omits materials, normals and
line geometry.

Use a new output path. Reader errors, an empty model, or geometry that the writer rejects stop the export.
Reader warnings are printed to standard error. The tool moves the completed cache into place after writing.

## Build from source

Run the optional tool from the RvtIO repository with the SDK in `global.json`. The build restores NwcIO from NuGet.

```sh
dotnet run --project tools/RvtIO.Nwc -c Release -f net10.0 -- model.rvt model.nwc --native
```
