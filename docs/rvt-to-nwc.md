# RVT to NWC

## Autonomous export

Run the optional tool from the RvtIO repository. The build restores NwcIO from NuGet.

```sh
dotnet run --project tools/RvtIO.Nwc -c Release -f net10.0 -- model.rvt model.nwc --native
```

This mode writes placed triangle geometry, face normals, diffuse colours, the element tree, IFC GlobalIds
and Revit property display text. The cache uses feet internally. It opens in NwcIO and was validated in
Navisworks Manage 2027. Export uses managed .NET code on Windows and Linux.

## Autodesk Navisworks

On Windows, pass the installed Navisworks Manage or Simulate executable to export a cache for Navisworks.

```powershell
dotnet run --project tools/RvtIO.Nwc -c Release -f net10.0 -- model.rvt model.nwc --navisworks "C:\Program Files\Autodesk\Navisworks Manage 2027\Roamer.exe"
```

RvtIO reads the RVT and exports a temporary IFC using its current IFC profile and Revit property sets.
Navisworks creates the NWC and opens it before the tool moves it to the requested path.
The cache's geometry and selection tree follow Navisworks' IFC import. The command requires a working
Navisworks installation. Tested with Navisworks Manage 2027.
The tested 2027 cache uses an IFC property record layout that NwcIO's managed reader does not yet support.

Autodesk documents the [command-line switches](https://help.autodesk.com/cloudhelp/2022/ENU/Navisworks/files/GUID-3891FB9F-7BF1-4510-A1EE-121E975C0ACE.htm) used to create and open the cache.

## NwcIO preview

The optional `RvtIO.Nwc` tool uses `RvtIO.Scene` and the NwcIO writer. Run it from the repository with
the SDK in `global.json` and either supported .NET runtime.

```sh
dotnet run --project tools/RvtIO.Nwc -c Release -f net10.0 -- model.rvt model.nwc
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
