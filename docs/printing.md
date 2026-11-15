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

A view can be printed instead of a level, at the view's own view scale:

```sh
rvtio print project.rvt views.pdf --view "Уровень 1"
rvtio print project.rvt views.pdf --view "Уровень 1,Уровень 2"   # one page each
rvtio print project.rvt --list-views
```

`--list-views` prints the id, name, scale and level of every printable plan view. A plan view is a `DBViewPlan` of the
file whose name matches a printable level (or whose stored elevation equals the level's); a section or an elevation is
left out until its renderer exists.

The library API is `RvtIO.Print.PlanPrinter`: `Levels(document)` returns the printable levels, and
`Render(document, levelId, scale, options)` returns a `PdfDocument` with one page.

## What is drawn

| Content | Source |
|---|---|
| Walls | the stored location line, drawn as two sides its stored width apart with end caps (`WALL_ATTR_WIDTH_PARAM` of the wall or its type; 100 mm when neither stores one) |
| Openings | the cut loop of a hosted door or window, transformed to the model, leaves its stretch of wall out; the wall is drawn in the pieces between its openings, with a jamb at every cut, and a window gets a light line across the opening |
| Columns | the stored bounding box footprint, as a filled rectangle |
| Grids | the stored location lines, clipped to the extent of the level (a grid line can run far beyond the building) |

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
