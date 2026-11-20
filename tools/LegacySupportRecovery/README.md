# Recover legacy analytical supports

This maintenance tool restores native Revit 2021 point supports on columns and area
supports on foundation slabs. It requires the original project, a downgraded
Revit 2021 copy, and a Revit 2021 donor prepared for that same project. Run it
on work copies. The tool writes a new file and refuses an existing output path.

## Prepare the donor

If the downgrade opens but rolls back analytical edits because its spatial
index has no container, save an empty project in Revit 2021 and initialize
the index in a work copy:

```sh
dotnet run --project tools/LegacySupportRecovery -- initialize downgraded.rvt empty-2021.rvt initialized.rvt
```

Open the initialized copy in Revit 2021 without Audit. In a separate donor
copy, create a native analytical column or slab with the original axis or
external contour and support settings. Use the original physical type for
each prototype. Place each boundary condition on its analytical host and
confirm Revit creates its dependent marker. Use the required projection
datum if a slab is offset from its default plane. Save and reopen the donor,
then compare outlines, axes, restraint settings and host links against the
source. A pair of collinear source edges can be represented by a single
legacy edge when their covered geometry agrees.

Record the element IDs (decimal integers) in a tab-separated file with a
header and one row per original physical owner. Use the following column names
separated by literal tabs:

```text
physical	donor_physical	donor_host	donor_support	donor_instance	source_host	source_support	source_instance
```

The columns, in order, are:

1. Original physical owner present in the downgrade.
2. Donor physical prototype.
3. Donor analytical host.
4. Donor boundary condition.
5. Donor marker instance.
6. Source analytical host.
7. Source boundary condition.
8. Source marker instance.

## Restore and verify

```sh
dotnet run --project tools/LegacySupportRecovery -- restore initialized.rvt original.rvt donor-2021.rvt mapping.tsv recovered.rvt
```

The restore step binds the native analytical records to the retained physical
owners, brings over the required analytical nodes and projection datum, and
uses the source creation episodes and original element IDs for the requested
hosts, supports and markers. It keeps the native numeric IDs used internally
by the copied graph. The tool checks the archive, owner identities, ID
collisions, frame checksums and record references before writing.

Open the result in Revit 2021 without Audit and without saving. Look up every
requested element by its source `UniqueId`. Compare the analytical hosts and
support geometry with the source, check that each support links to the
retained physical element, and review the journal for model errors, removals,
automatic repair and `RecoverUniqueElements`. The caller supplies the
verified donor geometry and mapping. The downgrade CLI does not invoke this
maintenance tool.
