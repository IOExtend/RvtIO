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
output path (also a level name that is not printable), 2 the file cannot be read, 3 a reader limit cut a stream, 4 the
level has nothing to draw.

The library API is `RvtIO.Print.PlanPrinter`: `Levels(document)` returns the printable levels, and
`Render(document, levelId, scale, options)` returns a `PdfDocument` with one page.

## What is drawn

| Content | Source |
|---|---|
| Walls | the stored location line, drawn as two sides its stored width apart with end caps (`WALL_ATTR_WIDTH_PARAM` of the wall or its type; 100 mm when neither stores one) |
| Columns | the stored bounding box footprint, as a filled rectangle |
| Grids | the stored location lines, clipped to the extent of the level (a grid line can run far beyond the building) |

The page is white with a frame at the margin, and the drawing is centred. Elements are selected by their level
(`LEVEL_PARAM`); a wall without a location line or a column without a footprint is left out and counted by neither
selection nor drawing.

## What is not drawn yet

- **Openings**: doors and windows do not cut the walls, so a wall is drawn solid through its openings.
- **Floors, roofs, ceilings, stairs, furniture** and every other model category.
- **Rooms**: the reader decodes no room boundary loops for these files, so no room boundaries or names are drawn.
- **Annotations**: text, dimensions, tags, symbols, revision clouds.
- **The view's own crop and view range**: the drawing is the whole level, not the part a Revit view shows, and the cut
  plane of the view is not applied.
- **The sheet**: no title block, sheet number or name, no viewport placement, no multiple views on one page. This is the
  next part of the work; see [the sheets and viewports catalog](sheets.md) for what is decoded of a sheet today.
- **Text**: the PDF writer has the standard Helvetica font only, which holds no Cyrillic; labels (grid names, room
  names, the title) wait for an embedded font.

## Roadmap

1. Walls cut by the openings of their hosted doors and windows, and door and window symbols at the cut.
2. Floors and roofs as their boundary loops; stairs and furniture as their projected footprints.
3. The view's crop and view range, so the drawing is the part the view shows at the view's cut plane.
4. A TrueType font embedded in the PDF for Cyrillic text: grid labels, room names and the title.
5. Sheet composition: page size from the title block, the view placed at its viewport position, the title block and the
   sheet fields, several views on one page.
