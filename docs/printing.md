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

A view can be printed instead of a level, at the view's own view scale; plans and compass elevations are both accepted
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

## Rendering views

A rendering view (a 3D view Revit rendered) prints its image: Revit stores the JPEG in the block of the image symbol's
type (class 0x0890), the reader finds it among the partition streams and the PDF writer embeds it with the DCTDecode
filter without decoding the picture. A sheet places it at the offset of its viewport, keeping clear of the title block
strip.

## Printing a site plan

A view named "Site" or "Site Plan" is drawn from the site itself, not from the walls of a level: the topography is a
light green wash with its outer edge inked (the edges shared by two mesh triangles are dropped), each planting is a
circle with four spokes at its position and size, and the walls of the level with most walls stand on the same plan as
the building footprint.

A planting schedule under the plan lists every kind of plant with its count.

## Printing an elevation

`rvtio print project.rvt elevations.pdf --view South` prints an elevation from the 3D geometry: every planar face of the
model (walls, floors, roofs, columns, windows and doors) is projected on the vertical plane of the view, painted white
from the farthest to the nearest and outlined when it faces the viewer. Plants, topography and site surfaces are left
out: in an elevation they print as symbols, and their tessellated faces bury the building.

The plane of the view itself is not decoded yet, so the direction is taken from the view name (North, South, East,
West). A view named "Section" is projected along the short axis of the building, the way a longitudinal section looks; it
is a projection, not a true cut through the model. Measured on the Revit sample project: the South view prints
the two-storey facade with its balcony rail, columns and roof on sheet A103.

## Composing a sheet

`rvtio print project.rvt sheets.pdf --sheet A102` composes a sheet the way Revit prints it: the page is the size the
title block spans (the extent of the title block instance's type, snapped to A4 to A0), every placed viewport draws its
view at the offset the file stores for it, each view is titled as Revit titles it: the circled viewport number, the view name, its scale and a rule over the title, and the sheet's own text notes
are written at their positions.

Measured on the Revit sample project (rac_basic_sample_project.rvt, six sheets A001 and A101 to A105): all six pages are
841 x 594 mm (A1, the size of the "A1 metric" title block), and sheet A102 draws "Level 1" at (180, 453) mm and
"Level 2" at (530, 453) mm at 1:100, the positions the reference PDF export shows. The linework of the title block family is not decoded yet: the composer draws the page border and a title block strip
on the right of the page, built from the sheet's own parameters: a logo area, the project area, a revision table
(No. / Description / Date) and the sheet information (name, number, issue date, drawn by, checked by).

## Printing a sheet

```sh
rvtio print project.rvt sheets.pdf --sheet 516334
rvtio print project.rvt sheets.pdf --sheet 245623,516334   # one page each
rvtio print project.rvt --list-sheets
```

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
| Grids | the stored location lines, clipped to the extent of the level (a grid line can run far beyond the building), with a white bubble and the grid name at both ends |
| Floors | the sketch loops of the floors of the level, light under the walls |
| Rooms | the floor face of the room's decoded boundary, filled with a colour by the room name and tagged with the room number in a circle, the name and the area in m² |
| Doors | the leaf at one jamb, perpendicular to the wall on the side the door opens into, and the quarter arc it sweeps |
| Furniture | the horizontal faces of the furniture, casework, plumbing fixtures, specialty equipment, stairs and railings of the level, seen from above as light outlines |
| Door and window marks | the Mark parameter of each door and window, in a small circle beside the opening |
| Dimensions | a chain between neighbouring grid lines above and to the right of the building, with the distance in millimetres |
| Room legend | below the plan: a colour swatch and the name of every kind of room on it |

The page is white with a frame at the margin, and the drawing is centred. The title (the level name and the scale) is
written with an embedded TrueType font found on the machine (Arial, Segoe UI or Tahoma on Windows, DejaVu Sans on Linux,
Arial or Helvetica Neue on macOS); without such a font the page is drawn without a title, because the standard PDF font
holds no Cyrillic. Elements are selected by their level
(`LEVEL_PARAM`); a wall without a location line or a column without a footprint is left out and counted by neither
selection nor drawing.

## What is not drawn yet

- **Door leaves and swings**: an opening is a gap with jambs; the leaf and its swing arc are not drawn.
- **Floors, roofs, ceilings, stairs, furniture** and every other model category.
- **Rooms**: the reader decodes no room boundary loops for these files, so no room boundaries or names are drawn.
- **Annotations**: text, dimensions, tags, symbols, revision clouds.
- **The view's own crop and view range**: the drawing is the whole level, not the part a Revit view shows, and the cut
  plane of the view is not applied.
- **The sheet**: no title block, sheet number or name, no viewport placement, no multiple views on one page. This is the
  next part of the work; see [the sheets and viewports catalog](sheets.md) for what is decoded of a sheet today.
- **Annotations text**: grid names, room names and dimension text are not drawn yet; the title is.
- **The view's cut plane**: the level is drawn whole, not cut at the view range of a Revit view.

## Roadmap

1. Door leaves and swing arcs, and window frames with their mullions.
2. Floors and roofs as their boundary loops; stairs and furniture as their projected footprints.
3. The view's crop and view range, so the drawing is the part the view shows at the view's cut plane.
4. Grid labels, room names and dimension text with the embedded font.
5. Sheet composition: page size from the title block, the view placed at its viewport position, the title block and the
   sheet fields, several views on one page.
