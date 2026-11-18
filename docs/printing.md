# Printing a plan

`rvtio print` renders the plan of one level of a Revit project to a PDF page without Revit. This is the first view
renderer of the sheet printing work described in [issue #18](https://github.com/IOExtend/RvtIO/issues/18): it draws the
model geometry the reader decodes, not the linework Revit generates for a view. It is a working drawing of the level,
not yet a printed sheet.

## Use

```sh
# the levels that can be printed, with their elevation
rvtio print project.rvt --list-levels

# the plan of one level at 1:100 on the smallest paper that holds it
rvtio print project.rvt plan.pdf --level 355 --scale 100

# a fixed paper size
rvtio print project.rvt plan.pdf --level "Уровень 1" --scale 200 --paper A3
```

`--level` takes the element id or the exact level name (case-insensitive). Without it the level with the most walls and
columns is printed. `--scale` is the view scale denominator: 100 prints 1:100, one model metre to ten millimetres of
paper. `--paper` takes A4, A3, A2, A1 or A0; without it the smallest landscape paper that holds the drawing at the scale
is chosen, and a drawing larger than A0 is scaled down to fit. Exit codes: 0 written, 1 usage error or an unwritable
output path (also a level or view that is not printable), 2 the file cannot be read, 3 a reader limit cut a stream, 4
nothing to draw.

A view can be printed instead of a level, at the view's own view scale; plans, compass elevations, sections, 3D views
and renderings are all accepted
(one page per view, in the order given). `--all-views` prints every printable view and `--all-sheets` every numbered
sheet:

```sh
rvtio print project.rvt views.pdf --view "Уровень 1"
rvtio print project.rvt views.pdf --view "Уровень 1,Уровень 2"   # one page each
rvtio print project.rvt --list-views
rvtio print project.rvt all.pdf --all-views     # every plan and compass elevation
rvtio print project.rvt sheets.pdf --all-sheets  # every numbered sheet
```

`--list-views` prints the id, name, scale and level of every printable plan view and the id, name, scale and direction
of every compass elevation. A detail view of a plan (a DBViewPlan drawn at 1:2 to 1:24) is left out: it is not the plan
of its level. A plan view is a `DBViewPlan` of the
file whose name matches a printable level (or whose stored elevation equals the level's); a section or an elevation is
left out until its renderer exists.

The library API is `RvtIO.Print.PlanPrinter`: `Levels(document)` returns the printable levels, and
`Render(document, levelId, scale, options)` returns a `PdfDocument` with one page.

## Images

The title block family's logo is read from the file: Revit keeps the placed image files in the partition streams, so the
reader decodes the PNG it finds there (8 bits per channel, RGB, RGBA or palette) to an RGB raster and the PDF writer
embeds it as an image object with Flate compression. The logo is fitted into the logo area of the title block strip with
its aspect preserved. The API is `PdfRasterImage` and `PdfPage.DrawImage`.

## 3D views

A 3D view placed on a sheet (a rendering that has no image, a solar analysis) prints as an isometric projection of the
model from above the south west corner, white faces with their outlines: it is the massing the view would show, not the
shaded picture Revit renders.

## Rendering views

A rendering view (a 3D view Revit rendered) prints its image: Revit stores the JPEG in the block of the image symbol's
type (class 0x0890), the reader finds it among the partition streams and the PDF writer embeds it with the DCTDecode
filter without decoding the picture. A sheet places it at the offset of its viewport, keeping clear of the title block
strip.

## Printing a site plan

A view named "Site" or "Site Plan" draws only the planting tags and the north arrow over the paper: the reference
site plan shows no building, terrain, contours, planting graphics or entourage, only the tags with their leaders
(the fall of the ground reads from its spot elevations, the planting from its schedule). Each kind of plant is
tagged the way the planting schedule tags it (T1, T2, ... with the real type marks) on a leader line from the
plant, a north arrow stands at the lower right of the site, and a planting schedule under the plan lists
every kind of plant with its count.

## Printing an elevation

`rvtio print project.rvt elevations.pdf --view South` prints an elevation from the 3D geometry: every planar face of the
model (walls, floors, roofs, columns, windows and doors) is projected on the vertical plane of the view and painted
white from the farthest to the nearest, so nearer mass covers what stands behind it. Only the shared edge set is
inked: an edge two coplanar faces share is a triangulation diagonal inside a flat surface and is left out, every
other edge is drawn once, and an edge whose midpoint hides behind nearer mass is occluded. Coincident modeling
collapses to one line: parallel edges within 50 mm at the same depth and of the same length (a railing family
drawing one board four times, a window jamb doubled by its frame) ink once, keeping the nearest; a short edge on
a long one stays, lines at different depths stay, and staggered layers stay, so distinct linework is never lost.
Windowed details skip this and keep every fine construction line. A face counts as nearer
by its nearest corner (a wall end turned away from the view still covers what stands behind it), and a midpoint on
the boundary of the nearer face counts as inside (half a millimetre): stacked wall layers and window frames project
onto the silhouette of the mass in front, and the reference draws that silhouette once. Plants, topography and
site surfaces are left out of the building itself: they print as light shapes without outlines behind it, the faded
context the reference export shows around an elevation (their faces of a few square metres, so the ground mesh does
not bury the building). Where the viewport decoded an axis-aligned crop box, the drawing is filtered to its rectangle
at generation time (the scorer counts vectors, not the page clip), and levels, grids and the outline follow it. The
stored rectangle is origin-relative view feet (rvt-prt's viewer_frames), while the collected content runs in absolute
projected millimetres, so the filter offsets it by the view origin's projection; without the offset the frame lands
right but the wrong slice of the model is drawn (the stair viewport missed by 146 mm, the 1:20 detail by 365 mm).

The level lines are thin and dashed (3 mm on, 1.5 mm off on the reference). Each spans its decoded datum
envelope while the datum stays near the crop; where the datum runs far past a narrow crop (the stair at 1:50),
the line is clipped to the crop plus a label-strip margin on each side, as the reference ends its lines at the
markers just past the frame. Each line ends in the marker the reference export uses:
a circle with an arrow head at the right of the drawing, the level name above and its height in millimetres below
(Roof Line 6000, Level 2 3000, Ceiling 2700, Level 1 0, Level 1 Living Rm. -550, Foundation -800 on the sample).
Only the level records of the project are drawn: the families each carry their own reference levels under the same
category, which no project view shows. Each height is printed once even though the project holds many level elements
at the same height, and the drawing reserves a label margin on the right. The grids dash coarsely ([36 9 9 9] pt,
resolved from the grid type's center-segment line pattern) with solid white-filled bubbles, as the reference does.

The plane of a view is decoded from the view's own block (class 0x3F8): the block stores a point of the plane, the view
scale, the vertical axis and the camera direction, so an elevation is projected on the exact axes of the view and a
section is **cut at its plane**: the geometry between the viewer and the plane is removed, the polygons crossing the plane
are clipped, and the faces behind it are filled grey. The stored point is file feet, like every model coordinate, and is
converted to millimetres before the cut. On the sample, the Longitudinal Section of A103 shows the rooms,
walls and the stair cut at the section line. A view whose plane is not decoded falls back to the direction taken from its
name (North, South, East, West, Section projected along the short axis of the building). The edges the cut itself
creates ink with their own category's cut pen (a railing cuts thinner than a wall, falling back to the
Walls cut pen where the category decodes none); every other projected edge of a section inks thin with the projection pen,
as the reference draws cut boundaries heavy and projection linework light. Grids are datum elements: they ink with the
annotation pen table (pen 1 is 0.1 mm at every scale on the reference), not the scale-dependent model table.

A detail section (1:10 to 1:50) is printed when its plane is decoded: the cut is drawn inside the view's
own file crop frame (the detail's true extent: a roof detail's crop sits at roof height), clipped to it at
generation time. Without a usable crop the cut falls back to a window of about 3.0 x 2.2 metres around the
point of the plane. A detail the file parks against the border or outside the page is still printed: Revit lays
such callouts out in a bottom row above their own titles (A104's four details), so the frame is anchored on the
decoded title label instead of the file offset. A stair detail (1:20) instead takes
the file's own crop frame, the way the reference places it next to the stair section; it keeps its grid bubbles
and the level lines whose height falls in its crop (Level 1, Ceiling, Level 2: the three bands the reference draws
at the same heights; the bands run to the label margin past the crop, while the reference ends each at its own
marker). A view the file places off the page
(a stair section whose stored centre is x = 0) is moved into the free space of the sheet, a drawing larger than its
place is drawn at a scale that fits, and every drawing is kept inside the page. Two drawings the file places on top of each other are separated: the later
one moves to the free place of the top row. A view of the section class whose name carries no compass word (a "Main
Stair Detail") is printed as a section too. The plane of a view is looked for in
every chunk of the file, and the block layout (class 0x3F8) takes precedence over the older signature, so the details are
found as well as the building sections. A viewport whose view is not printed (a wall section at 1:20, a schedule) is
neither drawn nor titled, so a sheet shows only the views the printer can stand behind. A stair section (a view whose name holds "stair", 1:20 to 1:50)
projects only the stairs, their railings, supports, lines, treads and risers, so the page shows the stair rather than
the whole building; the tread/riser boards are the mass the occlusion pass needs, otherwise the stair-body projection
edges stay inked where the reference hides them (483 faces and 35 stair edges with them, 440 and 121 without).
Measured on the Revit sample project: the South view prints
the two-storey facade with its balcony rail, columns and roof on sheet A103.

A drawing the layout reduces to fit its place prints at the scale it finally has: its view title, the sheet scale
row and the plan page title name that scale, not the nominal one.

A plan's room colour fills carry the floor material's surface pattern where the file stores one: the floor type's
first compound layer (the top layer) names the material, `Material.m_surfacePatternId` names the pattern element, and
the pattern's `FillGrid` records give each line family's angle, origin, shift/offset and dash list in feet. A drafting
pattern (target 0) keeps its paper lengths, so the dashes print at their stored size whatever the view scale; the
reference draws the sunken living room's carpet as a `Sand` hatch inside the room's green fill, and the printer clips
the dashes to the room polygon the same way.

A stair belongs to both plans of its flight: the base level draws it going up and the top level draws the flight
arriving there (`STAIRS_BASE_LEVEL_PARAM` / `STAIRS_TOP_LEVEL_PARAM`). Its plan linework is the outlines of its
horizontal faces (tread and landing tops) seen from above, so the Level 2 plan carries the tread courses the
reference shows between the stair's sides.

A plan also draws the elements below its level that its view range shows. `PlanViewRange2` is a nested object of the
view's block (not an element record): five offsets in feet (cut plane, top clip, bottom clip, view depth and a
reserved value) and the five level ids they are measured from. The printer takes the band between the bottom and top
clips and inks the railing top rails whose geometry stands inside it, which is how the observation deck's rail — on
the roof below Level 2 — reaches the Level 2 plan as the double line along the deck edge.

## Composing a sheet

`rvtio print project.rvt sheets.pdf --sheet A102` composes a sheet the way Revit prints it: the page is the size the
title block spans (a composed sheet keeps that paper whatever `--paper` says; the option applies to plans and views) (the extent of the title block instance's type, snapped to A4 to A0), every placed viewport draws its
view at the offset the file stores for it (the viewport record also stores the reciprocal of the view scale, used when
the view's own scale parameter is missing), each view is titled as Revit titles it: the circled viewport number, the view name, its scale and the extension line under the title, drawn from the viewport record's own label origin and length (m_labelOrigin, m_labelLength) in the annotation pen, and the sheet's own text notes
are written at their positions.

Measured on the Revit sample project (rac_basic_sample_project.rvt, six sheets A001 and A101 to A105): all six pages are
841 x 594 mm (A1, the size of the "A1 metric" title block), and sheet A102 draws "Level 1" at (180, 453) mm and
"Level 2" at (530, 453) mm at 1:100, the positions the reference PDF export shows. The linework of the title block family is decoded from the symbol (its GLine objects, in symbol feet at the sheet position, each in its graphics style's annotation pen); the fixed texts (TextNoteData + RtfData) and labels (TagNoteData, resolved from the sheet and project parameters) are drawn at their anchors; where the linework decoded the composer draws it instead of the page border and the title block strip
on the right of the page (built from the sheet's own parameters and the project information (the organization, the
project name and its number come from the project information element when the project information stream does not carry
them), plus: a logo area, the project area, a revision table
(No. / Description / Date) and the sheet information (name, number, issue date, drawn by, checked by)).

## Printing a sheet

```sh
rvtio print project.rvt sheets.pdf --sheet 516334
rvtio print project.rvt sheets.pdf --sheet 245623,516334   # one page each
rvtio print project.rvt sheets.pdf --sheet A102 --where "Drawn By=AB"
rvtio print project.rvt sheets.pdf --all-sheets --where "Appears In Sheet List=Yes"
rvtio print project.rvt --list-sheets
rvtio print project.rvt --list-sheets --where "Sheet Group=Architectural"
```

`--where NAME=VALUE` (also `NAME!=VALUE` and `NAME~TEXT` for contains, `*` and `?` wildcards, repeatable,
case-insensitive) keeps only the sheets whose parameters match, as in `rvtio sheets --where` (see
[sheets and viewports](sheets.md)); with `--sheet` a sheet prints when it is one of those numbered and matches
the filters. A misspelt name or an empty match exits with code 1.

A sheet is printed from the linework the reader decodes: the location curves of the elements the sheet owns (the page
border, the title block frame and its rules, the tables drawn as lines), at their sheet coordinates. The page is the size
those curves span, snapped to A4, A3, A2, A1 or A0 when it is within 8 mm of one (a sheet whose curves span
210 x 297 mm prints on A4); otherwise the page is the extent plus a margin. The sheet number and name are written in the
upper-left corner with the embedded font.

`--list-sheets` prints the id, number, name, extent, curve count and text count of every sheet with a page-sized
linework (a frame smaller than 50 mm is the drawing frame of an embedded family document, not a sheet).

The text of the sheet is printed too: the reader decodes the RTF string of every text note (class 0x0F83 block) into
plain text, with the position the block stores in feet and the font size the RTF declares, and the page writes each note
at that position with the embedded font. A title block therefore prints with its labels ("Изм.", "Кол.уч.", "Лист",
"Согласовано" and so on) and the sheet number and name are written in the upper-left corner. The text alignment inside
its box and any rotation are not decoded, so a note is drawn from its stored position as a left-aligned baseline.

A placed view is not composed into the page yet (its viewport has no decoded position), so the drawing area of a sheet
stays empty.

## What is drawn

| Content | Source |
|---|---|
| Walls | the stored location line, drawn as two sides its stored width apart, the stretch between them poche (filled grey) with end caps (`WALL_ATTR_WIDTH_PARAM` of the wall or its type; 100 mm when neither stores one) |
| Openings | the cut loop of a hosted door or window, transformed to the model, leaves its stretch of wall out; the wall is drawn in the pieces between its openings, with a jamb at every cut, and a window gets a light line across the opening |
| Columns | the stored bounding box footprint, as a filled rectangle |
| Grids | the stored location lines, clipped to the extent of the level (a grid line can run far beyond the building), drawn dashed with the file's center-segment pattern like elevation grids, with a white bubble and the grid name at both ends |
| Floors | the sketch loops of the floors of the level, light under the walls |
| Rooms | the floor face of the room's decoded boundary, filled with the colour the reference plan uses for that kind of room (master bedroom blue violet, master bath magenta, bedroom cyan, linen teal, hall blue, entry hall yellow, living light green, kitchen tan, laundry brown, mech green, bath green) and tagged with the room number and the area in m² in a rounded white box at the middle of the room, as the reference plan tags it (the legend carries the names) |
| Doors | the leaf at one jamb, perpendicular to the wall on the side the door opens into, and the quarter arc it sweeps |
| Furniture | the horizontal faces of the furniture, casework, plumbing fixtures, specialty equipment, stairs and railings of the level, seen from above as light outlines (a stair names its base level, STAIRS_BASE_LEVEL_PARAM, not a level) |
| Door and window marks | a door's Mark in a circle beside the opening, a window's type mark in a hexagon, each on a leader line from the opening |
| Dimensions | a chain between neighbouring grid lines on all four sides of the building, with the distance in millimetres, as the reference plan dimensions it |
| Room legend | below the plan: a colour swatch and the name of every kind of room on it |

A plan viewport whose view decoded an axis-aligned crop frame draws only that rectangle at generation time, the way elevations do (the scorer counts vectors, not the page clip): walls, furniture and opening frames outside it are dropped, their crossing edges cut without inking the crop boundary, like the reference cuts linework at the viewport frame. Grids keep running past the crop to their bubbles, as the reference draws them to the bubble row.

The page is white with a frame at the margin, and the drawing is centred. Lines print with the weights of the document's Object Styles at the view scale (walls and columns cut with the Walls cut pen, section faces with the cut pen, elevation faces with the projection pen, grids and furniture with their own pens); without a decoded table the previous fixed widths stay. The title (the level name and the scale) is
written with an embedded TrueType font found on the machine (Arial, Segoe UI or Tahoma on Windows, DejaVu Sans on Linux,
Arial or Helvetica Neue on macOS); without such a font the page is drawn without a title, because the standard PDF font
holds no Cyrillic. Elements are selected by their level
(`LEVEL_PARAM`); a wall without a location line or a column without a footprint is left out and counted by neither
selection nor drawing.

## What is still not drawn

- **The crop region of a view**: a plan or elevation viewport whose view decoded a crop box draws the crop at the view's own scale (the frame is the crop rectangle, placed by the viewport offset); the axes the view was collected with must match the crop basis. A detail section (1:10 to 1:50) is drawn from its own file crop frame the same way, and a view without a decoded crop, with the crop region off or with a rotated basis keeps the whole-model frame.
- **The four details of A104**: the "Typical Wall Roof Connection", "Typical Floor Wall Connection", "Typical
  Foundation Detail" and "Typ. Wall Section" viewports are placed outside the page by the file, so they are laid
  out in a bottom row above their own decoded title labels, the way Revit places such callouts.
- **A schedule's body rows**: the block of a schedule view stores its visible columns (the "How do I" schedule of the sample
  stores Level, Topic, What view, Reviewed?; the revision schedule No., Description, Date; the planting schedule
  Type Mark, Common Name, Count), its shown sections with the title height, the merged title bands and the body rows
  with their stored texts, but not evaluated body cells: Revit evaluates them from the model when it draws the schedule.
  Every per-sheet schedule instance prints at its instance origin with its decoded columns, show flags, grid and outline
  pens (the negative category ids they name, resolved against the Object Styles), title bands and body grid; revision
  rows come from the model revisions explicitly present on that sheet (the sample lists none, like the reference),
  padded with empty rows to the decoded fixed height (ScheduleSegmentRec: 21 rows at the measured 16.30 pt pitch
  on the sample) the way Revit fills the table to its height; and planting rows group the placed instances by
  their resolved type (type mark with the T{n} fallback, common name from the type), with the stored leading
  empty rows and a header that wraps into its columns like the reference.
- **The table grids of the title block family**: the linework, fixed texts and labels of the symbol are drawn, and the
  per-sheet schedule instances (revision tables, planting schedule, How-do-I table) print from their decoded view
  blocks; the symbol-embedded schedule template itself (the cached revision grid with GINFO_NOT_PRINTED) is left out.
- **Property and setback lines**: the site plan prints the topography, the contours, the planting and the entourage, but
  the property and setback lines (OST_SitePropertyLineSegment) carry no geometry the reader decodes.
- **Images other than the logo and the renderings**: material textures and the sun-path of a solar analysis are not
  pictures the writer can place; an analysis view (Solar, Lighting, Energy, Sun Path) prints no vector massing,
  like the reference, which shows no such linework.
- **Annotation elements**: grid names, dimensions, room tags and the section marks are generated by the printer from
  the decoded elements, not read from the file's own annotation elements; a section mark prints only where its
  section answers to no view or to a plan at a scale no coarser than its own (wall-section callouts and fine
  details stay in their parent views, undecodable views mark nothing), only when the file gives it an extent
  record (the Architectural Section has none, so it marks nothing instead of inventing a trace); a decoded
  front-cut polyline draws its jogs (the Building Section turns at its jog instead of running the full frame
  height) while a mark without decoded points stays a straight ray; the trace, its ticks and its bubbles ink
  with the Sections projection pen (pen 1 annotates 0.1 mm on the reference, where the old hardcoded 0.22 mm
  inked every trace heavy); the trace is clipped to the viewport frame at generation; the view reference
  marks of a plan stay undrawn. One measured exception: the Longitudinal Section's mark is drawn 3,090 mm along
  +X from its decoded front cut, because the reference places it there while the Building Section's sits exactly on
  its own cut (control: both at x = 592.1 pt) and no stored field explains the difference (`m_pntForViewData` is the
  first front-cut point, `m_rotOffset.x` half the cut length, the view origin 3,280 mm — 190 mm away, nine times the
  measurement error).

## Next steps

1. Decode the crop region of a rotated view and of a crop region switched off with live bounds, and the annotation crop that keeps titles and labels outside the model crop. Decode the record that places a viewport the file puts off the page. Assign section-mark ends per viewport (the Longitudinal trace matches the reference length but sits ~88 pt left of it with Y exact and walls aligned, so the shift is trace-specific with no file rule found yet; the reference puts a numbered callout bubble "3/A103" at its start while ours centers letter bubbles on the ends; Level 2 shows only a 171 pt stub near the stair where the file extent predicts 599 pt; the short callout bubbles with sheet references).
2. Evaluate the body rows of element schedules from the model (the How-do-I reference body is computed, not stored).
3. Decode the geometry of the property lines (with their bearing/distance labels) and the spot elevations, the solar-analysis diagram of a site sheet, and the file's own tag positions with leaders.
4. Decode per-view placement of viewports and datum annotation: our stair/detail frames follow the file offset while the reference resolves overlapping viewports side by side, and the reference lines its level marks up on the crop edge and ends each line at its own marker while ours run datum spans (clipped only past a label margin). Restore the long footing/grade model edges the file's footing instances never decode into geometry. The stair railing boards the reference omits in its stair views, and the interior edges behind a section cut (A103's 207 in of extras), are **not** a hide record: `m_oHiddenElementsViewSettings` (2160), `m_pHideElementsMgr` (2163) and `m_explicitlyHiddenElements` are null/empty on every view, no `HiddenElementsViewSettings` block exists in the model (the 2160 class tags in the view bodies are null-pointer annotations, each preceded by an `FF FF FF FF` token), and the railing ids (567887, 665711, 490129, 566588) appear in no view body. Both differences are Revit's coarse-detail model representation (all views here are detail level Coarse), which the file does not store as data — a printer rule would be a guess, so neither is implemented.
5. Widen the same printing to the releases the reader supports in its own schema.
## Comparison with the reference export

Measured on the sample project `rac_basic_sample_project.rvt` against the reference export of the same file
(`rac_basic_sample_project.pdf`, six A1 pages):

**Measurement caveat.** Four of the six pages carry the drawing itself as a raster image: `get_image_info`
reports 81 tiles covering 100% of pages A101, A103, A104 and A105 (A102 has one near-zero-area image and is
effectively vector). Extracting a tile (A103, xref 142) shows the complete linework — walls, foundation hatch,
slabs, ground line. The vector layer therefore holds only part of the content: on A103 it has 461 in of
horizontals, almost all of them the dashed level rows and sheet/title rules, while the vector page A102 has
924 in. `rvt_prt compare` reads only `page.get_drawings()`, so on those pages it scores our vector print against
a partial layer. A rule accepted on that score alone is measuring an export artifact, not the reference print:
the horizontal-edge, buried-work and railing-diagonal removals were reverted for exactly that reason. Judging
those pages needs a raster-aware comparison (or a vector-only export of the reference).

| Sheet | Printed | Still different |
|---|---|---|
| A001 Title Sheet | the title texts, the five renderings from the file, the logo, the "How do I" table with its title, long-text band, header and body grid at its instance origin, and every revision table with its header and empty body grid to its fixed height | the evaluated body text of the "How do I" table (computed by Revit, not stored) |
| A101 Site Plan | the planting tags T1... with their leaders, the north arrow, the planting schedule with Type Mark / Common Name / Count in a ruled table at its instance origin, and the Solar Analysis viewport's title line and label (the study itself is a raster, so the viewport has no vector frame; its title is sheet annotation the reference prints) | the property and setback lines (no decoded geometry yet), the spot elevations, the solar-analysis diagram, the file's own tag positions |
| A102 Plans | both plans with the room colours of the reference, the room fills' floor surface patterns (`Sand` in the living room, from the carpet material of the sunken floor type's top compound layer, decoded to its line families and printed as drafting-pattern dashes), furniture as the plan silhouette (the boundary of the union of its faces on a 5 mm grid, so a chair keeps its back and the gap between its legs), stairs on both plans of a flight (the tread courses of its horizontal faces: base level going up, top level arriving), the observation deck's top rails from the view range (the rails stand on the roof below Level 2; the decoded PlanViewRange2 top and bottom clips decide which are visible), door and window tags, the room legend, the room tags, the grids dashed, and the section marks of the viewports they belong to (cut to the viewport frame at generation); one dimension chain above the building, 780 mm beyond the outer wall face as the reference draws it (measured 22.1 pt at 1:100), placed from the walls the viewport actually draws so off-crop retaining walls cannot push it off the page | the view reference marks (annotation elements whose positions the reader does not decode); the Building Section trace repeats in the Level 2 plan, whose own mark the file hides elsewhere; the short per-element dimensions of the other three sides; the Level 2 chain sits 24 pt below the reference's (22 pt above its face on Level 1 but 46 pt on Level 2 - a user-placed offset with no decoded rule) |
| A103 Elevations/Sections | South elevation and the Longitudinal Section cut at its true plane (feet-to-mm), level markers, the faded site; stacked wall layers hide on the silhouette, coincident modeling inks once, coplanar triangulation diagonals not inked, crop-filtered at generation; every visible edge inks, horizontals included — the wall top, the slab and roof outlines, the sills and the ground line all print, as the reference page shows (its drawing on this sheet travels as a raster image, so the vector layer alone lacks those horizontals and must not be used as the target); a section keeps both its cut profile (the roof slab at z=6898/7178) and the projected horizontals behind the plane; work below the lowest decoded level prints too (the foundation walls and their hatch down to z=-6302); level lines follow their datum envelopes near the crop with a label-margin cap for narrow viewports, and the grids dash coarsely ([36 9 9 9] pt from the grid type's line pattern), as the reference does | per-view level ends and markers (not decoded) and the long footing/grade model edges (footing instances lack decoded geometry). The 207 in of 0.283 extras are the interior seen behind the cut, attributed per category on the Longitudinal Section (model inches: walls 4,290, roofs 2,303, floors 2,043, building pad 1,904, casework 1,737) — Revit's coarse-detail representation, which the file does not store (see next step 4); the pairs 3.4 pt apart are the two faces of one wall, which coarse mode draws as a single line |
| A104 Elev/Sec/Det | West elevation and the Building Section cut at its plane, the four detail sections drawn from their own file crop frames in a bottom row above their titles, each with the annotation curves the view itself owns (`OST_Lines`, `OST_InsulationLines`, `OST_SketchLines` whose `OwnerViewId` is the view; clipped to the crop so a hidden line past its edge neither prints nor stretches the frame); cut-created edges ink heavy, projection edges thin | the fine placement of the four details (laid out from the decoded title labels with a 20 mm gap while the reference varies); the remaining in-detail 2D linework, which is not stored as view-owned curves (`OST_DetailComponents` holds 264 geometry-less attribute records, and the generic `OST_Lines`/`OST_SketchLines` curves name no owner) |
| A105 Elev/Stair Sections | Stair Section cut at its true plane with restored level lines, Main Stair Detail on the file's own crop frame with grid bubbles and the three in-crop level bands at the reference heights, East elevation, hidden lines removed; a railing's continuous rails print as the long diagonals they project across the stair, as the reference page shows, and its balusters stay; the crop filter runs in the true slice (origin-relative rectangle offset by the view origin); cut edges ink with their own category's pen; treads and risers included (they are `OST_StairsTrisers` and their mass hides the stair-body projection edges the reference hides) | the fine stair-profile linework the reference draws as coarse-detail graphics (this page's drawing is raster, so the vector comparison cannot judge it) and the level-line ends at the reference's own markers (bubble positions not decoded) |