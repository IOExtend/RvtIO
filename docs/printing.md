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

Axis-aligned floor plans on sheets print the supported circular elevation marker with one attached view.
The marker's position and body and pointer families come from the RVT. The pointer follows the view direction,
and the labels read its detail and sheet numbers from the placed viewport. The saved crop, cut height,
coarsest scale and hidden elements determine which markers appear.
On sheets, walls in a plan with a saved unshaded hidden-line style use a white base under their outlines.
At the saved cut height, closed wall sections use the model faces and their opening loops. The cut retains
mitred ends, jambs and shared-wall joins. Saved hosted opening spans remain clear even where the model section
has a solid face above or below the opening. Such cases use the wall-axis outline with its saved gaps.
A wall below the cut and within the saved plan range uses its horizontal top face when that face covers the
wall's plan projection. The outline uses the Walls projection pen.
Opaque plans also retain the visible outlines of supported closed fixture components below the cut, even
when the same saved face belongs to more than one component. A rigid instance placement carries the
saved circular arcs into the view. Matching runs on horizontal face boundaries use the arc samples for
projection and occlusion.
Opaque plans clip supported closed generic-model instances between the saved bottom and cut planes.
The visible projection has a white mask, and the closed section uses the category's cut pen. Shared
projection spans use the cut pen once. Open geometry and nested cut rings are outside this path.
The projected outline keeps distinct edges of thin facets before testing them for coverage.
Plans also print closed horizontal detail circles with the default line style from the owning
view, using their stored centre and radius, view visibility and the Lines projection pen.
Plans cut supported closed curtain panels and mullions at the saved cut height and use their category cut pens.
Horizontal mullions below the cut and within the saved plan range project their top face with the projection pen.
Curtain outlines respect the viewport crop, category visibility, and hidden components and hosts.
Plans use closed column sections at the saved cut height, with the column category's cut pen and the
material's supported drafting hatch. A matching vertical cylinder in the placed symbol supplies a
circular outline. The bounding footprint provides the fallback when the cut is unavailable.
A straight section trace in an axis-aligned plan can print the supported filled head and tail. The view
type supplies the symbols, their bounds fix the size on paper, and the placed viewport supplies
the detail and sheet numbers.

## Images

The title block family's logo is read from the file: Revit keeps the placed image files in the partition streams, so the
reader decodes the PNG to an RGB raster and the PDF writer embeds it with Flate compression. The renderer follows the
title block symbol's GImage to its ImageSymbol and image holder, and uses the stored origin, dimensions and scale
for axis-aligned placements. The constructed title block strip keeps its existing logo fallback.

## 3D views

A regular 3D view prints as an isometric projection of the model with white faces and outlines. An analysis view
with a decoded orthographic camera and an active model section box prints the model at its saved sheet position
and scale, using the RVT material colours. The view's saved category visibility controls which model geometry
appears and casts shadows. The section box clips the geometry in model space. With patterns enabled,
its vertical terrain cuts use the soil material's drafting hatch and colour. The camera foreshortens
the saved paper-sized grid on each cut plane. The grid starts from the model origin projected onto
that plane.
RPC planting appears and casts shadows when its saved outline fits the active section box. Oblique
planes use the type outline in its saved instance placement when it reproduces the saved world outline.
Both sides of its polygon meshes draw and cast shadows. In the supported shaded lighting mode, an
unassigned mesh material uses a neutral gray shaded by the active sun and the visible side's normal.
In the supported shaded lighting mode with zero configured ambient light, the cut faces use a diffuse tint based
on the active sun direction, the saved sunlight setting and each face's normal. The hatch keeps its material colour.
For a multi-day study with fixed hours, the saved study settings supply the compass size, ground level, dates,
time range and active frame. The compass follows true north from the active project location. The latitude,
longitude and time zone drive the solar band and dates. Hourly studies show the active sun and its time,
and shade the exposed side of the solar band that faces away from the active sun.
The start and end dates mark the daily guides. The saved clock times and the geometric horizon form the
time boundaries of the study area. The active date supplies its geometric sunrise and sunset labels.
Opaque model geometry hides the compass ring and ticks where they lie behind the model on the saved ground plane.
In the supported shaded lighting mode, the exposed single layer of the solar band blends its sunlit tint
with the model or paper background using the same transmission.
When the view enables lighting and cast shadows, opaque geometry casts shadows on model faces, terrain and the
saved ground plane at the active hour using the saved shadow intensity. The section box also clips shadows on
the saved ground plane. The solar annotation can be hidden independently.
Graphics transparency is composited at each pixel in depth order over the lit geometry behind the panes. Surfaces
facing away from the active sun use the saved shadow strength as an approximation for their light level.
The view's surface-pattern switch controls continuous and dashed model grids anchored to their saved face placement.
The edge switch adds saved planar boundary loops. These markings follow the painted faces, so openings, foreground
geometry and the model section box hide the corresponding parts.
To include Revit's full rendered study, export and crop the viewport image above its title, then supply the image
and its sheet bounds to the composer.

```text
rvtio print model.rvt sheets.pdf --all-sheets --viewport-image ID X Y WIDTH HEIGHT image.jpg
```

`ID` is the placed viewport's element id. X and Y measure from the lower-left corner of the output sheet in millimetres.
The width and height use the same units. The API is `SheetComposeOptions.ViewportImages`. The supplied image
replaces the generated model image. The analysis viewport title comes from the RVT in both cases.

The option may repeat for different viewports. It needs `--sheet` or `--all-sheets`, and it is refused with
`--all-views` and the listing options. The ID must be unique, the width and height positive, the coordinates finite
numbers and the path not blank; a violation exits with code 1 and the usage text after a message about the option's
arguments. The file must be a JPEG of at most 64 MiB, and the viewport must be on one of the printed sheets; a violation
exits with code 1 and a message that names the viewport by its ID. All of these are reported before any page is
composed. A file that cannot be read exits with code 2 and `Cannot read viewport image <path>`. The PDF output path
must not be one of the images.

## Rendering views

A rendering view (a 3D view Revit rendered) prints its image: Revit stores the JPEG in the block of the image symbol's
type (class 0x0890), the reader finds it among the partition streams and the PDF writer embeds it with the DCTDecode
filter without decoding the picture. On sheets, a rendering is centred at its stored viewport offset and sized by
the image symbol's paper dimensions and the placed instance's scale. The default size and title block clearance
apply when the file's dimensions are unavailable.

## Printing a site plan

A view named "Site" or "Site Plan" draws the terrain, contours, building outline, plants, entourage and the
authored straight and circular property boundary segments. The segments follow the same rotated view basis and crop
as the terrain and keep their full extent when surrounding terrain is discarded. Plant tags use the schedule type
marks (T1, T2, ...). The sheet includes the north arrow and planting schedule.
Placed terrain, planting and entourage respect the site view's saved element and category visibility.

Cropped site viewports use the saved crop for the extent of the model. They draw stored primary and secondary
contours from placed terrain surfaces with their Object Styles pens. Supported contour labels use the stored
anchor, direction, text size and project length format. Planting instances use their saved insertion points.
RPC planting with a saved model height uses a circular plan mark with a radius of 27.5% of that height.
The 16 spokes use the shared profile measured on the Revit sample export and follow the saved view basis.
Planting without RPC metadata retains the bounds-based mark.
In lit elevations the saved RPC insertion point determines whether a plant casts shadows onto visible
geometry. An insertion point in front of the view plane keeps the terrain shadow contribution. An RPC that lies
entirely in front of that plane can also shade visible RPC foliage whose insertion point is inside the view.
Cast shadows follow the plane of a vertical wall face when it is oblique to the view and faces the light.
Planar mesh facets of placed structural foundations receive model shadows from surfaces whose saved
graphics appearance explicitly specifies opacity.
Supported planting tags use the family frame and label, the type mark, and the saved head and leader offsets.
Supported line-and-arc annotations such as the north marker use their family curves, pens and saved placement.
Coarse and medium site plans draw supported electrical and site equipment from saved top-view symbol curves
and masks. Masks cover terrain contours, and the saved lighting still shades the equipment. Visible site
equipment with a supported symbol can also cast shadows from its placed model faces.

Property boundaries fit their line pattern between the segment ends, with equal terminal dashes. Supported
segment tags use the saved anchor and family label offsets. Quadrant bearings in degrees, minutes and seconds
use the active project location. The displayed distance of a curved segment is its chord length, and its label
follows the tangent at the saved anchor. Distance labels use their family's explicit unit format.
A site view that hides the Property Lines category, the segments' own category or a segment prints neither the
boundaries nor their tags.

Opaque hidden-line site views print roofs within their saved view range. The boundary and skylight openings
come from the model face loops. The view projects planar glazing of visible hosted windows and clips the
white hidden-line fill to the roof openings. Where the view shows surface patterns, continuous model grids
use the face's saved pattern placement, with the face loops clipping the ink around openings. With relative lighting and a level
ground plane enabled, the saved sun angles project the roof shadow onto that plane at the view's shadow intensity.
The site view also projects horizontal treads and landings from visible placed stairs within the saved view
range. Nearer model and terrain faces clip the projection at their local depth. The visible stair surfaces
retain their cast shadows and use the saved projection pen for their outlines.
Visible horizontal slab tops use the same depth and shadow projection. The hidden-line glazing of roof
openings masks surfaces below the panes.
Placed top rails use planar faces from the visible railing's native geometry. The projected outline joins
the top faces, and their solid faces cast shadows onto the visible model and terrain.

## Printing an elevation

`rvtio print project.rvt elevations.pdf --view South` prints an elevation from the 3D geometry: every planar face of the
model (walls, floors, roofs, columns, windows and doors) is projected on the vertical plane of the view and painted
white from the farthest to the nearest, so nearer mass covers what stands behind it. Only the shared edge set is
inked: an edge two coplanar faces share is a triangulation diagonal inside a flat surface and is left out, every
other edge is drawn once, and an edge whose midpoint hides behind nearer mass is occluded. Coincident modeling
collapses to one line: parallel edges within 50 mm at the same depth and of the same length (a railing family
drawing one board four times, a window jamb doubled by its frame) ink once, keeping the nearest; a short edge on
a long one stays, lines at different depths stay, and staggered layers stay, so distinct linework is never lost.
Windowed details skip this and keep every fine construction line. Fine sections retain separated
projection edges from the same visible door frame face and add them after model-edge selection.
A face counts as nearer
by its nearest corner (a wall end turned away from the view still covers what stands behind it), and a midpoint on
the boundary of the nearer face counts as inside (half a millimetre): stacked wall layers and window frames project
onto the silhouette of the mass in front, and the reference draws that silhouette once. A window or door edge
can show through the wall identified by the insert's stored host ID. A skylight can show through the
decoded opening of its host roof face. For the host roof, depth is measured on the face at the projected
edge midpoint, so a nearer corner elsewhere on the slope cannot hide the window frame. Other roof faces
can still hide it. Fine sections and elevations that see the skylight glazing edge-on keep coincident skylight edges
at their model depths through occlusion.
Opaque frame faces hide farther edges and transparent glazing leaves them visible. The visible edges are
then merged in projection. Other inserts use a 500 mm clearance for their own faces to keep inset frame
details visible. Faces of other elements use the standard depth tolerance. At 1:100 and coarser, adjacent windows on the same host keep
their vertical junction when the incident faces project to opposite sides of it. Actual overlapping faces
still occlude. Elevations and sections omit viewport annotation geometry and skip discovered material,
constraint and reference plane meshes before projection. Native model elements that share the material
category are retained. Fine sections preserve
the distinct edges of cut stairs, runs and treads when consolidating coincident projections. In fine sections with
surface patterns enabled, an incomplete curtain assembly clears its fallback faces only over the corresponding,
face-on rectangular glass panes with decoded model fill patterns. Other cells keep the fallback geometry. Elevations and sections use the view's saved hidden element lists
before projecting model elements, datums and break lines. Fine sections clip projections at the closed,
face-on stair surfaces at their model depths and preserve the visible portions of partially covered edges.
The model crop limits fills and occlusion. Cropped section edges retain their original face boundaries, so
the crop rectangle adds no model strokes.
Plants, topography and
site surfaces are left out of the building itself: they print as light shapes without outlines behind it, the faded
context the reference export shows around an elevation (their faces of a few square metres, so the ground mesh does
not bury the building). Overview sections and regular fine sections with a usable model crop intersect the upper
terrain faces with the saved cutting plane and print the visible grade line. Projected model faces and the
footprints of solid building pads hide the grade they cover. At 1:100 and coarser, elevations print the
terrain profile at the stored view plane. The foreground portions of elements that reach the view plane
can hide this profile. Elements wholly outside the view leave the grade and hatch visible.
Closed intersections through walls, floors, foundations and pads draw their cut contours at the elevation plane
and leave their interiors clear of the soil hatch. In elevations that show surface patterns, supported drafting
grids from the cut material fill the closed profiles. Rectangular cuts through layered walls and floors use
each layer's material. The view origin anchors the patterns in paper units.
These elevations and sections hatch the ground between the profile and the stored poche base. The project
`TrueNorth` record supplies the base elevation and cut material. The material's cut pattern uses its decoded
drafting grids, anchored to the view origin. On elevations, the hatch follows the visible terrain spans and continues under the
pad at its stored elevation. In sections, the fill follows the cut terrain and pad profiles. Projected model
faces clip individual hatch strokes before PDF export. Overlapping and touching parts on the same hatch row
print as a single interval. The grade stroke is drawn over the hatch.
Where the viewport decoded an axis-aligned crop box, the drawing is filtered to its rectangle
at generation time (the scorer counts vectors, not the page clip), and levels, grids and the outline follow it. The
stored rectangle is origin-relative view feet (rvt-prt's viewer_frames), while the collected content runs in absolute
projected millimetres, so the filter offsets it by the view origin's projection; without the offset the frame lands
right but the wrong slice of the model is drawn (the stair viewport missed by 146 mm, the 1:20 detail by 365 mm).

Sections with an orthogonal jog in the saved front-cut polyline project each horizontal span from its
own cut plane and join the spans at the saved jog. The end planes extend to the crop beyond the plan
symbol's end marks. At a shifted cut plane, the printer clips the parts of projected strokes that
coincide with cut strokes. Levels, grids and view-owned annotations draw once across the viewport.

For the supported round datum heads, elevations and sections use the head symbol's saved dimensions.
Level names and elevations use the label style and position saved in the head family, including opaque backgrounds.
On Windows, regular 12 pt Arial labels use device text metrics for the text box. Other supported layouts use
the embedded font's metrics.
Levels use the view's stored datum ends and leader points where supported. A right-only level override keeps
the saved model extent at the left. A level with model extents uses its saved leader elbow and anchor.
When those anchors agree for levels of the same type and projected envelope, straight levels of that type
with the same envelope use the shared head column. Grids use the saved vertical bounds or that
view's own endpoint overrides. Authored annotations can extend outside the model crop, and their dash
patterns balance the two ends. For sections with an active section annotation crop,
default datum ends are clipped using its stored paper offsets around the model crop. The supported level
heads and top grid bubbles use their saved family sizes at the resulting endpoints.
Other heads and views without usable records use the existing fallback.

A cropped construction detail can show a child callout on the same sheet. Its child crop supplies the rounded
boundary with round-ended dashes. The saved parent-view head point places the straight leader and tag.
The viewport supplies the detail number. In the roof connection, the view-owned batt insulation supplies its
axis and width, and the computed break-line symbols supply the stretched break path and masking side. The mask clips the covered model
and insulation strokes before they reach the PDF. Perpendicular wall cuts and horizontal floor and ceiling cuts use
stored drafting patterns in the matching material bands when both sides and the exact layer stack are available.
In opaque cropped details those bands also place the layer boundaries, including layers without a cut pattern.
Where closed wall and roof cuts share a boundary with material on opposite sides, the shared span uses the projection pen.
When solid cut edges with different object-style pens overlap, the wider pen inks only the span contributed
by its source cut. The narrower edge keeps its own pen outside that span.
Sections at 1:50 or larger with a usable model crop use the same cut patterns through the regular section
projection path, including cropped stair sections. The site context in fine sections uses placed elements
and is clipped at the same section plane.
Cropped construction sections also cut and hatch the site through their saved section plane.
The printed grade stays inside the model crop. The hatch uses the site material and clips around cut foundations.
Its stroke width comes from the Topography Surface cut pen at the printed view scale.
In opaque hidden-line construction sections, closed wall, floor, foundation, pad and layered roof cut
profiles use a white base. The base covers projected surfaces and clips the spans of model edges behind the cut.
Material patterns and cut edges print above it. Faces with decoded openings preserve the projection through
the opening when the cut rings close. In aligned opaque fine sections, wall material hatches follow
those rings around junctions and openings. A perpendicular cut shows the stored roof layer boundaries
when the opposing faces match the total layer thickness. Two slopes that share a ridge join at the
intersection of their offset planes. The verified roof section supplies the white base and the bounds
of each layer's drafting cut pattern. A saved Fine view with an aligned model crop also enables these
roof layers at smaller print scales. Roof layer boundaries print after the model edges have been resolved. Valleys, nested rings and incomplete
geometry use the fallback. In aligned fine sections, a stair with one decoded face material and closed
cut profiles uses that material's drafting cut pattern. The stair body's profiles supply the hatch once
even when a modeled landing shares part of the cut.
When two walls have the same single face material, a simple closed cut of one wall clips the interior spans
of the other's cut edges. The outlines and layer lines print at their own boundaries.
In opaque cropped details, view-parallel wall faces without openings clip horizontal wall projection edges
at their actual outlines when the whole face is nearer. The remaining spans use the verified source plane
to calculate depth at the new midpoint. An overlapping wall on the same plane then keeps the shared outline visible.
In cropped sections, the cut stroke covers coincident spans of farther projected lines when its pen is at least
as wide. Uncovered spans and lines with a saved view line style stay visible.
Overview sections and regular fine sections with a usable model crop use white base surfaces for a saved
opaque hidden-line display. Other fine sections use white surfaces when both saved shadow modes are off.
In regular fine hidden-line sections, upright projected sides of structural floors use a white base.
For supported opaque floors, ambient shading combines nearby blockers with distance-weighted visibility
from the floor side to the section plane. Projected single-layer exterior structural walls use the saved ambient intensity
to approximate their tone above their saved base level. For samples below that level, the section crop
and the depth to the cut plane bound the ray reach for ambient occlusion.
A regular fine section also paints a white cut face for a closed wall solid where the section plane yields
a single closed ring. A fine section callout uses the same rule for closed floor solids. The cut face
covers the projected surfaces and edges behind that ring.
Section cut pens, model edges and material patterns are handled independently of the surface colour.
Fine sections with opaque hidden-line display and surface patterns enabled print supported drafting grids on the
faces that name their material. In a cropped model section, each continuous line uses the saved spacing and
view origin, and the face clips the combined vector fill. This keeps line positions independent of the PDF
reader's repeated-cell placement. Dense patterns and detail windows use a reusable PDF tile. Regular fine
sections use the saved widths for supported surface pattern pen categories at the view scale.
Nearer faces cover patterns behind them. The sample's Stair Section and Main Stair Detail use the
wood cladding's 3 mm vertical grid. On an otherwise unpatterned planar wall face, a saved paint region
can carry its own continuous drafting grid. Its boundary and the face openings trim that grid, while the
full parent face supplies the solid geometry. The Stair Section also draws its nested Main Stair Detail callout using
the saved boundary, head and sheet reference.
Elevations and sections that show surface patterns also project a supported roof's continuous model grid
using its saved face axes and origin. Skylight holes interrupt the grid. Parallel opposite faces of a closed
roof shell use their relative plane depth to keep the interior skin behind the patterned exterior.
A curtain assembly with every native panel resolved prints the placed panels, doors and mullions.
Openings from the inner loops of walls and elevation frames apply to both fill and edge visibility. Glass faces use their
saved model pattern placement and material's dash grid, with the same instance transform as the face.
In regular fine sections with the saved opaque display and ambient shadows enabled, curtain panels cover
geometry behind their glazing. Their printed tone uses the pane material and the view's saved ambient intensity
with local ambient shadow sampling. Other supported glazing blends visible backing surfaces in model depth order
within the pane.
Regular cropped fine sections with the saved opaque display exclude model geometry in categories hidden in that view.
When the project element table has been read exactly, these sections leave geometry discovered outside the table out of the projection.
Opaque fine sections with an exact element table project placed family instances and skip geometry directly owned by a family definition.
Fill pattern definitions supply material graphics for faces and are excluded from the model geometry pass.
A sloping wall edge at constant view depth keeps the spans outside closer opaque planar faces in
regular sections and callouts. Visibility is resolved in model face order before painting the
fills from far to near.
Round structural column shafts wholly behind the cut use the analytic cylinder of the placed symbol when its facets match.
The silhouette carries supported drafting surface patterns. Fine sections also use the saved material colour.
Opaque overview sections with surface patterns use the same silhouette. When the project element table is exact,
these sections use placed structural columns, casework, furniture, plumbing fixtures, speciality equipment and
structural foundations. In cropped overview sections, nearer placed terrain clips projected building pad and structural foundation faces
and the projected edges of building pads, walls and structural foundations behind the cut.
The printed terrain uses the same placed facets clipped at the section plane. Small facets inside the crop
fill gaps in the background, and the cut profile and hatch still draw over the projection.
Overview sections retain coincident stair edges at their original depths until visibility is resolved.
Stair and railing projections compare their depth with the depth of a planar wall at the sampled point
on each edge.
Horizontal tread edges also use the sampled depth of oblique vertical stair faces in the same native
assembly. The serialized owner connects the parent stair with its placed runs and tread components.
Opaque overview sections draw the projected silhouette of supported placed assemblies with one native revolved
mesh and closed solids wholly behind the cutting plane. Nearer opaque faces and cut masks clip the silhouette.
Regular fine sections outline placed specialty equipment made of multiple native revolved meshes when the
decoded shells are closed and lie behind the cutting plane. Edge-on auxiliary faces are allowed. Nearer faces
clip the outline, and the individual facets of the equipment do not add seams.
At fine detail, these overview sections draw the layer boundaries and saved material cut patterns of supported
closed floor sections. Rectangular compound cuts follow the layer thicknesses from the floor type.
View-aligned wall faces at the same depth with matching type and material mask projected edges through their
interiors. The section checks the trimmed spans against closer geometry.
Nearer opaque wall faces aligned with the view also mask the covered lengths of sloping railing edges
parallel to the view plane.
Edges at constant depth behind the whole shaft are clipped at its silhouette.
Closed floor, building pad and supported stair cuts cover projected surfaces and edges behind the cut.
In fine section callouts with ambient shadows, a view-aligned wall face uses its saved graphics colour
when its opaque material matches an explicit finish layer of the wall type and render appearance is disabled.
Core materials and category defaults use the usual wall base.
In a fine section callout, the saved parent view identifies the detail. Its panes composite the
material tint over the opaque view base, with surface marks and model edges drawn above them.
Opaque fine section callouts with ambient shadows also account for entirely glazed curtain panels
in a screen-space depth pass clipped to the saved crop. The pass adds the contribution of visible
panes to view-aligned walls with the default surface fill. The sampling extent follows the paper
scale and the added tone uses the remaining ambient light after the model-space shadow pass.
In the sample project's Hidden Line Main Stair Detail, the reference panel 977507 also changes the tone of
the retaining wall outside its projected outline. In the tested view, this tone is independent of the panel
material's graphics colour and transparency. The screen-space pass is an approximation of this effect.
In Revit, ambient shadows operate on the rendered view's image buffers. [Autodesk Revit API Developer's Guide](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API/files/Revit_API_Developers_Guide/Basic_Interaction_with_Revit_Elements/Views/Revit_API_Revit_API_Developers_Guide_Basic_Interaction_with_Revit_Elements_Views_Displaying_Graphics_with_DirectContext3D_html.html)
In controlled exports of the same wall and rectangular solid, the wall receives less darkening at
1:30 and 1:40 than at 1:20. At a fixed view scale, PDF export settings from 72 to 600 dpi preserve
the dimensions of the embedded shadow tile and the sampled wall's median tones.
Regular fine sections also approximate ambient shadows on supported planar opaque surfaces. Nearby
placed geometry reduces the ambient light at sampled points. Regular cropped sections include placed terrain
and other verified planar facets. An opaque hit inside the local sampling radius blocks its ray completely.
For an aligned inner finish of an exterior wall with an explicitly unattached top, only blockers with a
projected face contribute to its ambient shadow.
On a view-aligned exterior finish of a wall with its top attached, the occlusion contribution decreases
with the ray's distance to the blocker.
When the wall's saved top-attachment steps identify a roof, its faces are excluded from ambient
shading on that exposed finish.
Ambient rays from stair surfaces enter closed convex stair solids through their verified outward-facing boundaries.
Where the section plane intersects a stair, ambient shadow samples on its upright view-aligned risers use blockers with a projected face.
The saved ambient and direct light
levels determine how much of the surface illumination can be blocked this way. Samples behind
nearer faces contribute to interpolation on the visible part of the same surface. When the regular sample
grid misses a thin face entirely, its nearest boundaries supply the sample positions. The tone is clipped
to the surface and its openings, with hatches and edges printed above it. In opaque cropped details,
slab sides use visible samples at cut caps. The nearest visible sample supplies the texture tone behind the cap.
Cropped section details with
saved ambient shadows composite supported view-aligned glazed panels and transparent mass surfaces over
their projected backing. The surface facing the view applies the material transmission once, with its
model pattern and edges printed above it. On opaque walls in cropped details, the direct component
decreases with the angle of incidence. The ambient tone visible through nearer glazing follows the pane
boundary and its inner loops.
In opaque cropped details, planar walls, slabs and stairs occlude projected edges at
the depth of the overlap. Saved hidden categories contribute neither projected geometry nor cut outlines.
Coincident stair edges keep their individual depths until visibility is resolved.
Placed site objects print clipped silhouettes behind the model. Cropped opaque overview sections with surface
patterns, ambient shadows and a supported still solar setting shade crossed planting panels using the saved
date, location, project orientation and ambient and sunlight intensities. The diffuse tone uses either side of
each panel. Projected depth determines which parts of the panels receive the tone where they overlap. The
planting outlines print above the fills.
Discovered light-source construction faces are omitted from elevations and sections.
Regular fine sections with ambient shadows use the saved graphics colour for opaque planar fixture finishes when render appearance is disabled.
Fine sections retain small planar faces of placed lighting fixtures, including the facets of thin
suspensions. Coincident projections remain separate by depth until visibility is resolved.
Fully nearer solid faces without openings clip the covered spans of constant-depth fixture edges.
Elevations and sections print supported window and door opening symbols from the placed family's saved
lines. Paired direction filters select the visible side. A saved detail-level condition applies where present.
The graphics style supplies the width, colour and paper dash lengths. The symbol lines go through visibility
before they are added to the model edges.
When a saved elevation shows surface patterns, front-facing walls print supported drafting grids in paper
units from the view origin. Continuous grids leave decoded wall openings clear. Dashed grids print on
faces without openings.
Opaque hidden-line elevations with supported view-relative lighting project cast shadows from planar walls,
roofs, floors, stairs and placed railings, and from planting mesh facets, onto front-facing surfaces parallel
to the view. Opaque skylight frames cast shadows onto the sloped face of their saved host roof when that face
has decoded openings.
Placed structural columns cast shadows from their planar faces when the face material does not declare transparency.
The saved intensity controls the fill. Face openings and the visible terrain cut clip it, and model edges
and surface patterns print over it.
Patterned overview elevations draw the outline of a placed vertical cylinder, such as a flue, when its
paired native cylindrical faces match the placed facets and both rims span the projected tangents.
Wholly nearer opaque faces clip the covered lengths of the flue outline. Their decoded openings leave the
exposed edges visible. The saved light direction sets the edge of the cylinder's own shadow. Cast shadows
use the plane through the two projected tangents.
The saved view plane also clips the casters for shadows on model surfaces and plants.
The same lighting projects building and placed site mesh shadows onto sloping topography. Closer terrain facets
mask shadows on the terrain behind them. In cropped elevations, placed terrain also clips planar building faces,
their patterns and cast shadows, and the portions of projected edges known to lie behind the terrain.
A common vector fill joins the shadows across visible facets. In a cropped elevation the saved view plane also
receives shadows from planting facets in front of it that face into the view or are edge-on. RPC plants
whose saved element outline lies entirely in front of the plane are excluded from this cut shadow.
The ground cut retains the eligible shadows under its hatch and clips them to the soil profile and the
closed cut contours.
Opaque site plans with supported lighting project shadows from planar roof, wall, floor, stair, terrain and pad faces.
Opaque planar frame faces of visible hosted skylights in the saved view range also cast shadows.
The closest visible receiving surface above the authored ground plane receives the shadow, including sloping terrain.
Terrain edges also cast onto the ground plane outside the terrain mesh. The PDF joins the shadow facets in a single fill.
Roof shadows retain their receiving face and print above that face's fill and below its surface pattern.
Supported dashed drafting grids in the ground hatch use reusable PDF tiling patterns. The saved dash lengths,
row spacing, shift and origin define the tile lattice. In elevations the opaque hatched cut hides projected
faces and model edges behind the grade and pad profile. The topography cut pen adds ink on the soil side of a
closed building pad cut. The pad keeps its own cut pen along the full contour. Nearer opaque faces clip the
remaining spans of walls and foundations after the grade trims them. They also clip upright edges that project
into a building pad excavation.
Openings in the covering faces preserve the visible parts of those edges. In sections that show the cut terrain,
closed intersections of walls, floors, foundations and pads mask this ground layer at the cut plane. The layer covers
projected surfaces behind the ground, so a distant wall cannot hide the grade at the cut. Model projection
edges behind the cut are clipped to the visible side of the ground. When the section shows surface patterns,
supported dashed drafting grids on wall faces use the same PDF tiles and retain the projected face outline.
In opaque hidden-line elevations with a saved gradient background, the PDF shades from the sky colour at
the model crop's top to the horizon colour at model elevation zero. Where the cropped elevation uses terrain
receivers for cast shadows, the saved ground colour fills the crop below the horizon. The saved view plane
clips the terrain surface in elevations that cut the ground. Projected terrain and model faces cover the background inside the
model crop. Elevations at 1:100 and coarser clip projected model geometry and its openings at the saved
view plane.
Elevations and building sections at 1:100 or coarser with a saved view plane draw placed planting and
entourage from their projected mesh facets.
In opaque building sections at these scales, nearer planar furniture faces clip the covered spans of
window and door opening marks and constant-depth window jambs. Opening marks retain the dash pattern
and phase of the full family line.
When the view shows surface patterns, the planting pen also draws the open boundaries of triangulated
planting meshes. Shared triangle edges and edges created by the view cut stay hidden. The printed spans
are clipped behind closer planting and model faces and below the visible ground cut.
The view plane clips the mesh, the model crop clips its projection, and a joined fill retains the silhouette
through small facets without printing the internal triangle edges. An RPC planting whose saved element
outline reaches the view plane keeps its projected silhouette and visible edges when its entire display
mesh lies in front of that plane.
With supported cast lighting, the triangulated site meshes also receive shadows. Ray projection uses the
facets behind the saved view plane, and the closer plant facets mask coverage on plants behind them.
In those elevations, opaque model faces and site objects clip planting fills and their shadows according to
the depth at each intersection. Model face openings leave the planting behind them visible.
Placed site families retain the exterior boundaries of small mesh facets, including rotor blades.
Materialless entourage display meshes made of two perpendicular upright planes print their joined
silhouette. The printer recognizes the saved triangles, including an optional flat base. With saved
cast lighting, their visible faces use the shadow tone where the light reaches the opposite side.
Their fills and shadows are clipped behind closer geometry, including foreground planting. Other
materialless entourage meshes retain the edges of their visible triangles. At a section plane the
printer trims the original edges without adding a cut edge across a triangle. Site edges with the
same pen and projected endpoints are inked once after visibility and crop clipping. For a mesh
that lies fully behind the cut plane with only back-facing or edge-on triangles, the printer also
outlines the projected silhouette, without adding its triangulation diagonals.
In opaque vertical overview sections, a crossed entourage figure that intersects the cutting plane
prints whole. The nearer upright panel masks the farther panel where they overlap, and the figure
occludes projected model lines behind it.
Placed grouped electrical equipment in an elevation retains the visible outlines of its sloping planar
faces. The edge visibility pass measures depth at the edge and reads the nested material transparency
and the frame openings, so glazed arrays can show the geometry behind the cover.
The elevation uses the equipment material's authored shading colour when the RVT selects it instead of
the appearance asset. Transparent covers keep their edges without an opaque face fill.
Sections draw the silhouettes of supported round railing tubes from their placed cylindrical faces.
The tube profiles share the section cut, view crop and hidden-line pass used by the rest of the model.
In patterned overview elevations, verified horizontal and inclined tubes no wider than the projection pen print once along
their placed analytic axis. The view plane, nearer model faces and foreground terrain determine visibility.
The tube strokes join the drawing after projected mesh edges are resolved.
If the first projected copy of a stair edge is hidden, a coincident nearer edge can supply the visible stroke
after it passes the model and terrain visibility checks.
Fine sections trim projected door and window outlines only along the portions covered by a wholly
nearer railing face. The remaining jamb segments stay visible between the rails.
Fine model sections keep the physical depths of door edges through that visibility pass. Identical
visible spans ink once after the viewport crop. With ambient shadows enabled, doors without
transparent panels use the authored grayscale shading colour on opaque faces that select it.
Nearer railing faces and separately stored balusters clip the covered spans of projected rail edges.
In opaque fine sections, nearer slab sides facing the view clip the spans of upright wall and railing
profiles that they cover.
Closed furniture meshes with saved cylindrical facets print their projected contour in fine section callouts.
The projection skips edge-on faces with openings because they cover zero area.
In fine opaque cropped details, coincident vertical railing and curtain-wall mullion edges retain their native depths through clipping.
A nearer solid can mask the middle of a post while its upper and lower spans remain visible.
Fine sections retain separately visible edges of the same thin rail profile when consolidating
coincident strokes.
When several railing faces share a projected edge, a fine section checks their actual endpoint depths.
An edge shared by coplanar faces on both sides at every depth is an internal seam, including when the
railing and its embedded components repeat those faces.
In coarse sections and elevations with a saved view plane, coincident rail projections keep their distinct
depths until the visibility pass. Coarse elevations apply the same rule to walls, windows and doors. Nearer planting facets clip
the surviving segments. In elevations with a saved view plane, horizontal and vertical boundaries of the same visible railing
face remain distinct when their separation on the sheet exceeds the projection pen width.
For a single prismatic top rail viewed square-on, the printer checks the original face boundaries for
visibility before removing duplicate projected outlines. The view crop trims the source edges without
adding a cap where the rail crosses the crop boundary.

Fallback level lines are thin and dashed (3 mm on, 1.5 mm off on the reference). Each spans its decoded datum
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
generation time. The stored viewport offset locates the view origin, which can lie outside the page while
its crop is on the page. The mapped crop keeps its stored position whenever it intersects the sheet.
If the whole crop falls outside the sheet, the title provides the fallback anchor. Without a usable crop
the cut falls back to a window of about 3.0 x 2.2 metres around the point of the plane. A stair detail (1:20) takes
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
neither drawn nor titled, so a sheet shows only the views the printer can stand behind. A stair section with a usable
crop includes the surrounding building cut within the crop. The stair-only fallback for a small-scale stair section
applies when its crop is unavailable. The tread/riser boards are the mass the occlusion pass needs, otherwise the stair-body projection
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
arriving there (`STAIRS_BASE_LEVEL_PARAM` / `STAIRS_TOP_LEVEL_PARAM`). In a hidden-line view whose cut plane
crosses a single run with decoded linear boundaries, the printer uses the saved tread fronts and side paths.
The cut line goes through the last tread at or below the view's cut elevation, using the stair type's saved
angle and symbol size. The part below the line masks the room fill in white. The part above it uses the
stairs' above-cut projection styles and leaves the room fill visible. A straight run with both side paths
fully inside the visible range below the cut uses the saved tread fronts and a white mask across the run.
For a sketched run arriving at the plan's level below its cut, a stair path owned by the view identifies
the descending projection. The saved nosings and both side paths draw the run through its opening,
including treads below the plan's ordinary bottom clip. The joined side paths mask the room colour.
The attached top rail projects through the same opening when it lies below the plan cut plane. Its saved
top faces mask the parts of each nosing they cover at that nosing's elevation.
Other views and run layouts use the horizontal faces of the treads and landings.
An owned top rail with a decoded projection follows the host stair's cut line. The printer fills its
projected faces below the cut and inks the exposed boundary with the rail's projection styles.
On an upper floor plan, the view's own `StairsPathElement` places the automatic descending arrow for a
single straight flight below the cut. For a sketched flight crossing the lower clip, the saved centre
path runs from the last physical nosing to the first nosing at or above the view's cut offset measured
from the base level. The linked arrowhead type sets the open tip for both paths. A straight vertical
descending path can display its saved horizontal Arial label and text offset.

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
use the saved note frame for supported horizontal notes. The top of the frame and the font ascent give the
first baseline. The stored frame height determines the spacing of multiple lines. In rectangular model-cropped
plans with annotation cropping off, the grid envelope is clipped at the model crop plus a fixed paper-space margin.

Measured on the Revit sample project (rac_basic_sample_project.rvt, six sheets A001 and A101 to A105): all six pages are
841 x 594 mm (A1, the size of the "A1 metric" title block), and sheet A102 draws "Level 1" at (180, 453) mm and
"Level 2" at (530, 453) mm at 1:100, the positions the reference PDF export shows. The linework of the title block family is decoded from the symbol (its GLine objects, in symbol feet at the sheet position, each in its graphics style's annotation pen); the fixed texts (TextNoteData + RtfData) and labels (TagNoteData, resolved from the sheet and project parameters) are drawn at their anchors; where the linework decoded the composer draws it instead of the page border and the title block strip
on the right of the page (built from the sheet's own parameters and the project information (the organization, the
project name and its number come from the project information element when the project information stream does not carry
them), plus: a logo area, the project area, a revision table
(No. / Description / Date) and the sheet information (name, number, issue date, drawn by, checked by)).

Title-block notes use their RTF font size and stored alignment. Labels use the linked font's cap height, their
stored alignment and rotation, and the saved text width when wrapping is enabled. The scale field uses the
sheet's override when present or reports the printed scale. Sheets with multiple scales show "As indicated".

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
| Rooms | the largest horizontal face of the room's decoded boundary, including the boundary cache for curved walls, filled with the room colour and tagged with number and area. The printer derives area from that face when the room has no area parameter |
| Doors | the leaf at one jamb, perpendicular to the wall on the side the door opens into, and the quarter arc it sweeps |
| Furniture | the horizontal faces of the furniture, casework, plumbing fixtures, specialty equipment, stairs and railings of the level, seen from above as light outlines (a stair names its base level, STAIRS_BASE_LEVEL_PARAM, not a level) |
| Door and window marks | a door's Mark in a circle beside the opening, a window's type mark in a hexagon, each on a leader line from the opening |
| Dimensions | a chain between neighbouring grid lines on all four sides of the building, with the distance in millimetres, as the reference plan dimensions it |
| Room legend | the stored room legend instance anchors the first swatch, and its type sets the swatch dimensions. The printer sets text spacing relative to that size. Plans without a decoded legend keep the swatches below the plan |

Doors draw saved leaf lines and circular swing arcs using the computed family variant and its
instance placement. The plan's cut height selects the symbol's saved range. For direction-only
symbols, including doors in curtain walls, the cut must cross the placed door's vertical bounds.
Wall openings come from the family's cut loop. The swing approximation covers cut-loop openings
without a matching symbol.
A door or window that the view hides, by element or by category, prints no leaf, glass, frame outline, symbol or mark, but its
opening stays in the wall. The saved opening marks of hidden hosts are not printed, and a view that has saved marks of
its own gets no generated mark for the openings left untagged. Hidden stairs and railings, by element or category,
draw no tread or top-rail outline, and a top rail is hidden with its railing.
Projected roof-opening outlines follow the plan's hidden-element list and saved vertical range.
The printer checks the height of the loop after applying the instance transform, so a skylight above
the range leaves the floor plan clear.

In opaque plans, furniture silhouettes and their fills respect saved view-direction exclusions
along the family's vertical axis when the placement keeps it vertical. If the filter leaves no
projected model face, the printer keeps the model silhouette until it can read the substitute symbol.

Plumbing fixtures represented by saved 2D family geometry draw the visible symbol lines in their
saved graphics styles, placed by the instance transform. Opaque views also draw decoded background
masks from closed face loops. Both follow the plan view range and hidden-element list. At medium
detail, supported plumbing families whose model body is excluded from the top view draw their
saved top-view regions, masking surfaces and symbolic lines, arcs and ellipses. Furniture uses
the same projection where a convex saved mask covers the model faces that remain visible from
above.

Generic model families whose body lies below the plan cut use the projected silhouette of their
model faces, with the same visibility rules as furniture.
In an opaque plan, supported plumbing fixtures and generic families made of closed solids can also
show individual solid silhouettes and the rims of top openings. Surfaces above a line hide the
covered spans. Native surface tessellation supplies the outline when a folded polygon cannot be
triangulated in its local projection.

A plan viewport whose view decoded an axis-aligned crop frame draws only that rectangle at generation time, the way elevations do (the scorer counts vectors, not the page clip): walls, furniture and opening frames outside it are dropped, their crossing edges cut without inking the crop boundary, like the reference cuts linework at the viewport frame. Grids keep running past the crop to their bubbles, as the reference draws them to the bubble row.

The page is white with a frame at the margin, and the drawing is centred. Lines print with the weights of the document's Object Styles at the view scale (walls and columns cut with the Walls cut pen, section faces with the cut pen, elevation faces with the projection pen, grids and furniture with their own pens); without a decoded table the previous fixed widths stay. The title (the level name and the scale) is
written with an embedded TrueType font found on the machine (Arial, Segoe UI or Tahoma on Windows, DejaVu Sans on Linux,
Arial or Helvetica Neue on macOS); without such a font the page is drawn without a title, because the standard PDF font
holds no Cyrillic. Elements are selected by their level
(`LEVEL_PARAM`); a wall without a location line or a column without a footprint is left out and counted by neither
selection nor drawing.

Circular generic annotations with one solid filled region, a solid circular border and one horizontal text run
print on their owning sheet or viewport. Geometry, colours, pen weight, text placement and instance transforms
come from the RVT family and view. The annotation crop uses the saved paper offsets. This covers the sample's
41 help symbols, including the four on the rotated Site view.

## What is still not drawn

- **The crop region of a view**: a plan or elevation viewport whose view decoded a crop box draws the crop at the view's own scale (the frame is the crop rectangle, placed by the viewport offset); the axes the view was collected with must match the crop basis. A detail section (1:10 to 1:50) is drawn from its own file crop frame the same way, and a view without a decoded crop, with the crop region off or with a rotated basis keeps the whole-model frame.
- **The four details of A104**: the "Typical Wall Roof Connection", "Typical Floor Wall Connection", "Typical
  Foundation Detail" and "Typ. Wall Section" viewports are placed outside the page by the file, so they are laid
  out in a bottom row above their own decoded title labels, the way Revit places such callouts.
- **Viewport titles**: supported horizontal families supply the marker curves, label anchors, font sizes and wrapping.
  The viewport type controls title and extension-line visibility, and the viewport supplies the line placement.
- **Schedule evaluation**: the block of a schedule view stores its visible columns (the "How do I" schedule of the sample
  stores Level, Topic, What view, Reviewed?; the revision schedule No., Description, Date; the planting schedule
  Type Mark, Common Name, Count), its shown sections with the title height, the merged title bands and the body rows
  with their stored texts. For itemized instance schedules with flat sorting, text, integer and Yes/No parameters,
  and integer equality or inequality filters, RvtIO evaluates the body from the stored instance values. This covers
  the sample's 40 rows in "How do I". Unsupported schedules retain their stored body grid.
  The Russian print UI culture renders evaluated Yes/No cells as «Да» and «Нет». Text cells keep their stored values.
  The title, merged instruction bands and ordinary column headings use supported saved cell styles. The styles
  store cap height in logical pixels at 96 dpi. The printer uses the embedded font's cap height to convert to
  half-point sizes and applies the stored horizontal and vertical alignment.
  Evaluated rows use a column's horizontal alignment when all of its stored data rows agree. The regular Arial
  12-pixel body style retains its print row pitch and baseline when the saved cells share that style, including
  schedules that fill otherwise empty stored cells from evaluated values.
  Every per-sheet schedule instance prints at its instance origin with its decoded columns, show flags, grid and outline
  pens (the negative category ids they name, resolved against the Object Styles), title bands and body grid; revision
  rows come from the model revisions explicitly present on that sheet and pad to the fixed height stored in
  ScheduleSegmentRec. For supported regular Arial revision styles, the saved header and body text cap heights
  determine the row heights using the measured Revit ratio of 23/12. The heading uses its saved cell style,
  including when the title section is hidden. The sample has 21 empty body rows at a 16.299 pt pitch.
  Planting rows group the placed instances by their resolved type (type mark with the T{n} fallback, common
  name from the type), with the stored leading empty rows and a header that wraps into its columns.
- **The table grids of the title block family**: the linework, fixed texts and labels of the symbol are drawn, and the
  per-sheet schedule instances (revision tables, planting schedule, How-do-I table) print from their decoded view
  blocks; the symbol-embedded schedule template itself (the cached revision grid with GINFO_NOT_PRINTED) is left out.
- **Setbacks**: setback lines remain outside the supported property graphics. Supported property segment tags print their saved bearings and distances.
- **Analysis shading**: the generated analysis viewport uses graphics colours, graphics transparency, approximate
  dome shading and shadows from opaque model geometry. The supplied rendered image preserves Revit's rendering
  of appearance assets and full lighting.
- **Annotation elements**: plans print supported grids, dimensions, room tags and text notes from the owning view's
  decoded annotation data. Room-tag leaders support vertical attachments to a single centred label. Room area labels
  use the project area format and print the square-metre symbol as Cyrillic м² in the Russian print UI culture.
  A section mark prints only where its
  section answers to no view or to a plan at a scale no coarser than its own (wall-section callouts and fine
  details stay in their parent views), and a decoded front-cut polyline places the jogs. On an axis-aligned plan
  with annotation cropping disabled, the plan's `ExtentElem` supplies the continuous trace's endpoints as
  distances from the section's saved trace origin. The endpoints move along the first and last legs, and the
  trace can extend past the model crop. A trace without supported plan extents uses the stored front cut and
  clips to the model frame. A trace with no decoded points uses the available straight ray, and views without
  a section extent record leave no mark. The ink uses the Sections projection pen. Supported filled heads
  and tails use the section type's symbols and the placed viewport's detail and sheet numbers.
  Segmented section traces and view reference marks are outside the supported subset.

## Next steps

1. Decode the crop region of a rotated view and of a crop region switched off with live bounds, and the annotation crop that keeps titles and labels outside the model crop. Decode the record that places a viewport the file puts off the page. Decode the view-specific section extents, including the jogged Building Section and the short Level 2 stub.
2. Extend schedule evaluation to grouping, additional filters and computed fields.
3. Decode setbacks, spot elevations, and property tag leaders.
4. Extend datum support to left and dual level heads and additional head families. Resolve overlapping stair and detail viewports from their layout records. Recover the remaining footing and grade geometry and investigate residual interior edges and stair railing pieces.
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

**Raster score and residual attribution.** Rendering both PDFs at 100 dpi and scoring thin strokes with a 2 px
match radius gives, page by page (A001, A101–A105): recall 98.6, 94.2, 94.0, 95.8, 96.3, 92.6% and precision
99.1, 93.2, 96.9, 93.4, 95.6, 93.6%; tone correlation 0.96–1.00 over the inked blocks. The residual is not
missing geometry in most places:

- A105 scores 97.6/97.7% at 200 dpi and 98.8/99.7% at 300 dpi. Its largest gaps at the 100 dpi grid are thin
  lines that alias between pixel centres when our vector output is resampled, while the reference keeps them in
  its embedded raster. Nothing in the drawing is absent.
- On A101 the worst blocks (recall 4–37%) lie inside the Solar Analysis study. Each candidate source was
  measured and rejected: the decoded site contours ink there only as scattered lines (recall +0.00%, precision
  −0.26%), the pad faces project elsewhere in the view, and the terrain mesh boundary and its creases add ink
  the reference does not carry (recall +0.01% with precision −1.08% for the boundary, recall +0.1% with
  precision −1.2% for breaks above 60°). The remaining gap is the reference's own resampled raster: the
  anti-aliased edge of its 3D model, the model's stipple and tonal shading, and thin annotation lines.
- On A103 one block at the lower left (0% recall) is the topography's cut cap: a 13 m × 0.21 m section polygon
  at the section plane (world X −13.6…−12.3 m, Y −2.18 m, Z 0.83…1.04 m), drawn by us as an unhatched face while
  the reference fills it with a fine hatch. Its pitch does not match any decoded cut pattern (the site cut pattern
  is a 6.35 mm brick, the concrete cut pattern 6–11 mm, both sub-pixel at 1:100), so the pattern itself is still
  undecoded. The larger A103 gaps sit in the South elevation, where the guardrail's five horizontal bars are
  decoded (100 mm pitch, view depth −18200) but the occlusion pass drops them. Instrumenting the pass with the
  decoded polygons shows what actually covers each bar: only the railing itself (element 566588, faces 3 mm nearer
  at −18203) and its nested symbol (566589, 14 mm nearer at −18214). The clad wall (427092, "Wall - Timber Clad")
  does not reach the bars: its two faces span x = 13.06–13.26 m and z = −1.2…2.7 m, while the bars run at
  x = 7.06–13.06 m. The floor slab (550255, "Generic 150mm") spans the bars' x but sits at z = −0.70…−0.55 m,
  below them, and grazes only the bottom rail at z = −550. Exempting faces of the railing assembly
  (categories −2000126/−2000127 within 15–20 mm of the bar) restores the bars: A103 recall 95.8% → 96.1% and
  A001 98.6% → 98.7%, but A104 precision falls 95.6% → 93.8% for +0.3% recall and A105 falls 92.6% → 92.3% at
  93.6% → 93.3%, so the net score drops. Restricting the exemption to horizontal bars keeps A103 at 96.1/93.4%
  and A105 at 92.9/93.3%, yet A104 still pays 95.6% → 93.7% precision for the same +0.3% recall: the extra ink
  is the same bars drawn where the reference hides them (e.g. around 540–620 × 324–350 pt in the West elevation,
  where the bars of a lower railing coincide with a wall face). The occlusion stands. The duplicate copies are
  handled instead: the elevation merges the horizontal railing bars that project within a millimetre of each
  other and keeps the nearest copy, which lifts A001 98.6% → 98.7% and A103 95.8/93.4% → 95.9/93.5% without
  touching the other sheets (see the changelog).

- Why the reference shows the A103 bars but not their A104 counterparts. Instrumenting both views shows the
  added ink splits the same way: on A103 the un-hidden bars are 2171 pixels on the reference and 2173 off it, on
  A104 1149 against 1456; A104's off-reference ink is concentrated on the **bottom rails**, which the reference
  hides behind the solid base band while the thinner bars between the posts stay visible. The railing's materials
  are opaque — "Stainless Steel 1" and "Iron Gray" both decode transparency 0, so no glass pane is involved and
  the see-through rule cannot separate the cases. An extent-based exemption (the covering face must match the
  bar's own projected rectangle within 2 mm) leaves A103 at 95.9/93.5% and loses A001 0.1%: the covering face is
  a panel taller than the bar, not the bar's own strip. The reference draws railing rails as symbolic elevation
  lines, which is why its bars survive their own solid; reproducing that needs a railing-specific display rule
  rather than an occlusion exemption.

- The railing carries no symbolic elevation data to decode. Its type (490129, `StairsRailingAttr "SH_1100mm"`)
  stores no family and no geometry, and both railing elements (`566588 BaseRailing`, `566589 BaseRailingSym`)
  report zero `ViewFacingLines` and zero `Edges` while their 100–106 faces are the modelled rails. 118 other
  elements in the same file do carry view-facing lines (doors, planting), so the decoder is not the limit: the
  railing simply has none. The rails exist as faces. What hides them in the South elevation is the midpoint test:
  the bar runs from x = 7.06 to 13.06 m with its midpoint at 10.06 m, exactly behind the post at 10.05–10.07 m,
  so the whole bar is dropped. Clipping the bar around the railing faces it crosses restores it between the posts
  (the sheet now matches the reference's segmented bars) and measures A103 96.0/93.2% for the clip alone and
  96.2/93.2% with a 2 mm filter that drops clipped spans running alongside another rail. Both variants still cost
  A104 1.8–2.0% precision (96.6–96.7% recall against 95.6%) and A105 0.3%, because the bars that land beside a
  solid rail (A104's mid-rail) are not within the 2 mm filter either. The net score drops, so the clipping is
  reverted; a faithful fix has to clip per span against the real 3D occluders of each bar copy, not against the
  assembly's projected faces.

- Per-span clipping against every nearer face (the step above) measures A103 96.4/92.6%, A104 96.6/93.7% and
  A105 92.8/93.0% against the baseline 95.9/93.5, 96.3/95.5 and 92.6/93.6: recall gains 1.0 point in total and
  precision loses 3.3, so it is reverted too. The spans the reference omits are the bars that sit inside a solid
  rail — their projection is covered but their ends, which stick out a millimetre, survive a polygon clip. Only a
  point-in-solid test against the railing's B-rep shells can drop them; the decoder has no such test today, and
  the projected-polygon approximation cannot separate a bar inside the rail from a bar beside it.

- The point-in-solid test was built and answers the question. Ray casting (Möller–Trumbore, one oblique fixed
  direction, parity counted per shell so overlapping shells cannot cancel) over the railing's B-rep shells works:
  a shell's centre tests inside, a point 30 m away outside, and the 15 + 14 closed shells of `566588`/`566589`
  reduce to 379 triangles. Wired into the span clip it drops **nothing**: over the whole document not one railing
  bar span has its samples inside a railing shell, and the score equals the plain span clip (A103 96.4/92.6%,
  A104 96.6/93.7%, A105 92.8/93.0% against the baseline 95.9/93.5%, 96.3/95.5%, 92.6/93.6%). The bars the
  reference omits are therefore not buried in the railing's own mass — they are separate geometry the reference
  hides by some other rule, so a solid test cannot separate them. The feature is reverted; the remaining lead is
  to find which element those bars belong to and why the reference drops that element in the West elevation.

- The extra A104 bars belong to element 933840, `BaseRailing "SH_1100mm"` (category −2000126), one of the four
  railings of that type in the model. Instrumenting the drawn edges in the band 930–1180 × 315–380 pt names it
  directly: six horizontal edges at page y = 464…469 mm, one per 100 mm rail, all at the same view depth. The
  element is neither hidden nor filtered: `ViewHiddenElements` for view 8237 ("West") is empty, its created phase
  86961 is the view's phase, its demolished phase is −1, and the view crop is active and contains the bars. So
  the reference omits some of the railing's rails through hidden-line geometry, not through visibility settings,
  and the "hidden element" lead is a dead end. The railing is a legitimate, visible element; the difference is
  the same self-occlusion the earlier rules attacked.

- The 0%-recall block at 345–400 × 1060–1100 pt is not a topography cap after all, and no decoded pattern matches
  its hatch. Measured on the reference at 600 dpi the band is genuinely dark — mean grey 146 with 41% of its
  pixels below 140 — and at 1200 dpi its visible courses repeat every 12–14 px, i.e. 0.25–0.35 mm on paper. Every
  cut pattern in the file is four to twenty times finer: the soil brick (material 519) is 6.3 mm model =
  0.063 mm paper at 1:100, the concrete (711728) 2.6 mm = 0.026 mm, the coarsest is 6.3 mm = 0.063 mm. So the
  reference's texture is a resampled rendering of a sub-pixel pattern and the visible pitch is an alias; applying
  any decoded pattern would print a near-solid band, not that texture. The face itself is also not topography:
  instrumenting cut faces with a 12–14 m by 150–300 mm projected extent names element 512683, category
  −2000032 (OST_Floors) — a 247 mm floor-slab edge. Applying the site cut pattern to topography cut faces
  therefore never fired, and the print was unchanged (98.7/94.2/94.0/95.9/96.3/92.6% recall, precision
  99.1/93.2/96.9/93.5/95.6/93.6%). Hatching that block needs the slab's own section graphics, not the soil
  pattern.

- The slab's compound layers carry no cut pattern either. Element 512683 is a `Floor "Timber Suspended Floor"`
  (type 185144) with two host layers: layer 0 material 713090 "Interior finish" and layer 1 material 185148
  "Rendering appearance not upgraded". Layer 0 decodes a surface pattern only (429, twelve dashed segments at
  0.0067 ft = 2.0 mm), layer 1 decodes no pattern at all; neither has a cut pattern. The observed 12–14 px
  courses at 1200 dpi (0.25–0.35 mm) are a sampling beat, not a pattern: the 2.0 mm pattern is 0.02 mm on paper
  at 1:100, which is 0.945 px at 1200 dpi, and a 0.945 px period aliases to a beat of 1/(1−0.945) ≈ 18 px — the
  same order as the measured 12–14 px. The reference's texture is therefore the resampled 2 mm pattern, and the
  closest decoded pattern would print as a near-solid band rather than reproduce that texture. No decoded
  pattern matches the visible pitch, so nothing is applied and the print is unchanged.

- A105's largest zones (page y 1318–1382 pt, x 288–475 pt) are the East elevation's guardrail, not a raster
  artifact. At the scorer's 100 dpi the band loses 54% of its reference strokes, while a 400 dpi crop with the
  same 0.5 mm tolerance finds nothing missing: the difference is thin grey strokes whose antialiasing changes
  with the sampling grid. The 600 dpi comparison does show one real difference: the reference draws the
  guardrail's middle rail as a solid dark band, we draw only its two thin edges. Filling the railing's faces
  with their material colour (Iron Gray decodes `HasShadingAppearance` with no render appearance and zero
  transparency) was tried two ways and both lost precision: all railing faces gave A103 95.9/93.1%, A104
  96.4/93.1%, A105 92.6/92.9% with tone correlation 0.94 on A105, and restricting the fill to the thin rails
  (10–120 mm projected height) gave A103 95.8/93.0%, A104 96.3/93.6%, A105 92.5/93.0%. Neither raised recall, so
  the fill is reverted.

- Restricting the same fill to faces flat to the view (`facing > 1 − 1e-9`) changes nothing: the renderer defines
  `facing` as the magnitude of the normal's dot product with the view direction (`Math.Abs(...)` at line 854), so
  front and back faces both read 1 and the test cannot tell the visible side from the hidden one. Measured A103
  95.9/93.1%, A104 96.4/93.1%, A105 92.6/92.9% — the same numbers as the unrestricted fill. Separating the
  visible side would need a signed facing, but even then the fill covers the A104 railings that the reference
  leaves as thin lines, so the rule would have to key on the rail profile, not on the view direction.

- The rail profile does not separate the two either. A histogram of the railing faces flat to the East elevation
  (normal along the view) gives four profile heights in every `SH_1100mm` railing: 1297 mm and 1050 mm panels,
  a 50 mm rail, and strips of 0.4–2.7 mm. Element 933840 (the East guardrail) adds 416 strips of 0.4 mm, 566588
  (the site railing) 16 strips of 2.7 mm; both carry the same 50 mm rail. The earlier "thin rails" fill already
  tested exactly this profile: with the 10–120 mm window the only matching faces are the 50 mm rails, and that
  run measured A103 95.8/93.0%, A104 96.3/93.6%, A105 92.5/93.0% — recall down and precision down. A narrower
  40–60 mm window selects the same faces, so the test is already covered and the profile hypothesis is refuted.

- Resolution separates the alias from the geometry. Scoring the same print at 100, 200 and 300 dpi with the
  match radius scaled to the same 0.5 mm gives (recall/precision, A001…A105): 98.7/99.1, 94.2/93.2, 94.0/96.9,
  95.9/93.5, 96.3/95.6, 92.6/93.6 at 100 dpi; 97.2/98.5, 93.3/98.3, 96.1/98.4, 93.1/94.5, 95.5/95.8, 97.6/97.7 at
  200 dpi; 98.6/99.4, 27.7/97.3, 97.4/98.7, 95.5/98.0, 97.3/99.6, 98.8/99.7 at 300 dpi. A105 climbs to 97.6 and
  then 98.8, so its 7.4% is almost entirely the resampling of thin reference strokes. A101's 300 dpi figure
  (27.7%) is not usable: the reference page is a raster whose effective resolution caps below 300 dpi, so the
  comparison collapses; at 200 dpi A101 still lags at 93.3%, together with A103 at 93.1%. Those two pages carry
  the real gap.

- A101's largest block is not the property line. The saved property curves (`Element.SitePropertyCurve`) were
  projected into the shaded study twice, at z = 0 and at the terrain surface (sampling the toposurface triangles
  for the height) with the depth test. Both draw the boundary as a thin curve along the site edge — the same
  curve the reference shows at the foot of the green area — but neither reaches the meander: A101 recall stays at
  94.2% while precision falls from 93.2% to 92.8% (z = 0) and 92.7% (terrain height). The meander is a small
  closed stepped polygon about 9 × 14 mm on paper, whereas the property line is a long curve around the whole
  site. The terrain faces carry no inner loops either (`411452`, `490318`, `523063`, `800334` all report zero),
  so it is not a hole in the surface. Pads, contours, mesh breaks and holes are now all excluded by measurement;
  the element behind the meander is still unidentified, and every candidate projected so far adds ink without
  covering the reference's line.

- The meander is a closed stepped loop in the terrain, not a decoded boundary. With the solar-study camera
  (view 959071: origin 53.0/−141.0/161.7 ft, right 0.8849/0.4658/0, up −0.3837/0.7290/0.5668, factor 0.610 mm per
  foot, anchor 547.6/446.9 mm) the meander's page box 486.9–504.8 × 411.6–433.5 mm inverts to world ≈
  (−50, −133) ft at ground height. Only the two topography elements project into that box; no other element does.
  The property line runs through it — the site corner at (83.4, −135.5) ft projects to page (494, 399) mm — but at
  that place the reference draws a small closed loop (about 9 × 14 mm) while the six decoded property segments
  form the large site polygon and only a single straight segment crosses the box. So the loop belongs to the
  toposurface display (a contour loop or a subregion border), and our renderer draws no terrain edges at all;
  the contour sweep was measured earlier and added ink without matching. The meander remains unidentified.

- Enumerating the toposurface display records closes that lead too. The decoded data holds contours per
  topography element only: within 25 ft of the inverted meander position there is exactly one contour, at
  elevation −16.4 ft, a 40-point line that follows the western site boundary (`(-81.5,-169.3) (-81.7,-169.2)
  … (-81.6,-157.5)`), duplicated identically in both terrain copies `411452` and `800334`. That is a long open
  boundary line, not the meander's small closed stepped loop. No topography element is hosted, so no subregion
  boundary is decoded (`host = −1` on `411452`, `490318`, `523063`, `800334` and on the pads), and the file has no
  view-keyed display record for the 3D view (`PlanOutlineAnnotations` has no entry for 959071). The meander's
  source is therefore not in the decoded data; reproducing it needs a Revit-internal toposurface display record
  that the reader does not extract.

- The A101 tone gap is local shadow, not a global gradient. Per 8 × 8 block at 100 dpi the largest errors sit in
  the solar study, where the reference is far darker than us: block (1642, 288) reads 61 against our 187, block
  (1636, 386) reads 44 against 151, and (1699, 564) 138 against 231. Those are the kerb band and terrain patches
  that cast shadows darken in the reference. A fit over the whole study gives `ours = 0.789 · ref + 21.5`
  (residual 34.1 against 35.5), i.e. a lifted floor and compressed contrast. Moving the ambient floor
  (`SolarStudyOverlay.LightFloor`, 0.2 + intensity/100 · sunZ²) both ways makes the score worse: 0.1 gives A101
  93.3/92.7% with tone MAE 2.7, 0.3 gives 93.4/92.3% at 2.7, against 94.2/93.2% at 2.6 for 0.2. The global term
  is already at its best measured value, so the remaining tone error is the accuracy of the cast shadows
  themselves, not the gradient.

- The A101 shadow caster set is complete; the residual is the shadow's edge. Instrumenting `AddCaster` with the
  owning element and logging the hits over the kerb band (page 579,492 mm) names two casters: the retaining wall
  `599841` (`SWall "Retaining - 300mm Concrete"`, 19 hits, sun-projected box −205.6/−115.8 … −196.1/−106.9) and
  the RPC car `950367` (`M_RPC Beetle`, 686 faces, many small hits). Both are legitimate and both are in the
  caster list. The shadow does land on the band, but its edge sits two to three millimetres from the reference's:
  at (576,494) mm we read 120 against the reference's 151 (we are darker there), while at (579,492) we read 163
  against 76 (we are lighter). So the shade strength is now right and the caster set is right; the difference is
  the projected position of the shadow boundary, which follows the saved sun direction and the caster geometry.

- The sun vector is not the cause of the shadow-edge offset. Rotating the study's sun azimuth by ±3° and ±6°
  (an environment hook in `SolarStudyOverlay`) moves the A101 tone mean absolute error only between 5.095 and
  5.111 against 5.101 for the unrotated print — noise, and in the direction that would need about 30° to shift a
  shadow by the observed 2–3 mm, which would visibly change every other shadow in the study. The ephemeris is
  therefore not offset; the residual is the shape and softness of the shadow itself (the reference renders a
  feathered edge, ours is hard, and the caster set only approximates the building mass). The hook is removed.

- Feathering the shadow edge does not help either. A separable box feather over the cast-shadow mask, applied to
  the same receivers, gives A101 recall 94.3% at radius 1 and 94.2% at radius 2, against 94.4% unfeathered, with
  the tone MAE unchanged at 2.5 for all three and precision unchanged at 93.2%. The softer edge only removes
  pixels from the ink set, so the metric prefers the hard edge; the reference's penumbra is not what the score
  rewards. Reverted.

- The shadow-caster plane fit is a measured dead end. A face of a triangulated surface can be warped, and the
  stored normal then puts its plane through the first vertex up to 7.3 mm off the other vertices (element
  `701300`, the site surface, 1435 such faces). Fitting the plane to the ring's own winding and anchoring it at
  the centroid halves that (3.6 mm), but the reference's shadow follows the stored normal: A101 recall 94.4% ->
  92.5%, precision 93.2% -> 92.7%, tone MAE 2.5 -> 2.7. Reverted. The warped quad is a triangulated surface
  whose fold a single plane cannot represent, so the anchor stays at the first vertex.

- A solar study casts shadows from transparent surfaces too. The caster test was opaque-only, so the windows and
  the PV array's glass top (material `772432`, transmits) never entered the shadow pass. In a view with a
  solar-study shadow pass the test now ignores transmission; the added window shadows land down-right of the
  facade, on page 1555-1627 x 360-389 pt, where the reference is inked. Measured: A101 recall 94.4% -> 94.5%,
  precision 93.2% and tone MAE 2.5 unchanged; A001 and A102-A105 unchanged. Ordinary shaded views keep the
  opaque-only shortcut.

- Rendering the cast shadows after the sun-path dome raises A101 recall but costs precision. The dome blends
  75 % yellow over the model, which lifts a shadowed pixel to nearly the lit tone, and applying the shadow pass
  after the dome restores the contrast: A101 recall 94.4% -> 94.9% with 775 more reference ink pixels matched.
  The same pass overshoots that block: it carries 58.2k dark pixels at 600 dpi against the reference's 47.6k
  (the base print has 15.2k), and 196 of the new ink pixels lie outside the reference's strokes, so precision
  falls 93.2% -> 93.1%. Not kept.

- A103's 0 % block at 346-389 x 1058-1102 pt is the roof-connection detail's missing crosshatch, and its pattern is
  not decoded. The detail prints view `958303` (Typical Wall Roof Connection, 1:10); the roof cut there is one ring
  with a four-layer stack of 40/100/50/12 mm whose measured thickness matches the 202 mm sum, so the roof layer
  path itself runs. Only the inner 12 mm layer's material (`423`) carries a decoded cut pattern (three grids); the
  40 mm metal roofing (`232858`, the layer under the crosshatch, about 4 mm on paper = 39 mm at 1:10) has
  `CutPatternId = -1`, the 100 mm layer (`123310`) is absent from `MaterialPatterns`, and the 50 mm deck
  (`232856`) has none. Enabling the layer hatches for details (`!isDetail ? layerHatches : null` -> `layerHatches`)
  leaves every scored page unchanged, so the pattern is a category cut-pattern override (Object Styles, `OST_Roofs`)
  or view override that the reader does not extract; no element is owned by the view either, so it is not a filled
  region. The schema rules out the two nearest records: a graphics style (class 2046) carries only
  `m_materialElemId`, `m_penNumber`, `m_color` and `m_linePatternId`, and a category (class 629) only
  `m_categoryType`, `m_flags`, `m_name` and `m_parentCategoryId`, so neither names a fill pattern. The roofing
  material's own patterns are `cut = -1` and `surface = 232863`, a single solid grid at a 2 ft offset, which is not
  the dense diagonal crosshatch either. The block also prints the roof cut outlines pale against the reference's
  black, so both the pattern source and the cut pen need the project's Object Styles display table, which the reader
  does not parse.

- The view side of that decode is empty too. The view's `m_oGraphicOverrides` (view class 1016) is a
  `GraphicOverrides` object (class 2104) whose `m_fillPatternsCut` and `m_backgroundFillPatternsCut` are vectors of
  `pair<ElementId, ElementId>`; a temporary extractor parsed it for every detail view, and view `958303` and the
  other details all report zero entries. A scan of the file's whole material pattern table finds 27 materials with
  any pattern and none that references a dense crosshatch (`Diagonal crosshatch`, `Diagonal Crosshatch1`,
  `Rigid Insulation`, `Crosshatch`, `Kreuzschraffur`); the roof stack materials read `cut = -1/0/429`, and the only
  dense multi-grid patterns in use are `Concrete` (13 grids), `Earth` (6) and `Sand` (3) on the structural walls
  and pads. The block's crosshatch is therefore not reachable through the category, graphics style, view override
  or material records the reader holds; it belongs to view-owned annotation data (a filled region) that the
  reader does not load. The extractor was removed after the measurement.

- The frontmost-copy rule for the A103 rails is the sum of parts that were each measured. Selecting the frontmost
  copy is the echo pass already in the code (nearest of the copies that project within a millimetre, A103
  95.9/93.5%). Clipping each rail only by faces in front of that copy gives A103 96.4/92.6%, A104 96.6/93.7%,
  A105 92.8/93.0% — the bars print, but three sheets lose precision. Adding the "drop rails whose midpoint is
  inside a solid rail" test changes nothing at all (identical numbers): with the shells of `566588`/`566589`
  (29 closed solids, 379 triangles) no clipped rail span has its midpoint inside a shell, because the bars are
  separate boxes rather than rails buried in mass. The precision loss on A104 is not the solid test's to remove;
  it is the bars that sit beside the solid mid-rail and that the reference omits. Combining the three steps
  therefore reproduces the earlier measurement, and the rule is not committed.

- A103's largest 200 dpi gaps are the louvered low structure at the bottom left (page 331–374 × 1066–1080 pt,
  recall 0–6%). At 600 dpi the reference draws fine horizontal slats with diagonal ties and a stepped outline;
  our print draws the same polygons flat white with no texture. The face belongs to the floor
  `512683 "Timber Suspended Floor"`; its face material `185148` decodes no pattern at all, while the compound
  layer material `713090 "Interior finish"` carries drafting surface pattern 429 (twelve dashed segments at
  2.0 mm). Two fixes were tried: allowing the drafting surface patterns at any scale instead of only fine
  sections, and extending the dashed-pattern branch from walls to floors. Neither changed A103 (95.9/93.5%) —
  A105 even lost 0.1 — because the visible face resolves to the pattern-less material. The reference's slats are
  therefore a rendered texture of the floor assembly that the decoded face materials do not carry.

- Binding the floor layer's pattern to the face is moot: the louvered block is not a floor. In the section basis
  (view 247014, H = X, V = Z) the block's model box is x −14.5…−12.7 m, z 0.9…1.2 m, and the elements there are
  the solar-panel array `800325` `SunModule SW 245 Silver Mono - 10 Deg. Angle` (667 faces) with its instance
  group `800331` (2668 faces), plus terrain. The floor `512683` comes from a different view and area. The
  reference's slats are the panel frames and mounting rails; binding pattern 429 to a floor edge would have hit
  nothing, which is why the attempt printed byte-identical sheets. Drawing the panels' rails is the actual next
  lead for this block.

- Keeping the panel edges does not help either. The panels' faces do reach the face list (5237 faces of
  `800325`/`800331` are added with `outline = True`, `fineFace = True`, projected areas 180–13 000 mm²), so their
  edges enter the pipeline and are then absorbed by `ConsolidateCoincident`, whose 50 mm tolerance merges the
  rails that sit a few millimetres apart on paper. Exempting `OST_ElectricalEquipment` from that consolidation
  inks every rail but costs the sheet: A103 recall 95.9% → 95.4%, tone correlation 0.97 → 0.96 and MAE 3.0 →
  3.1, while A104 and A105 stay put. The reference draws a subset of the rails (its hidden-line selection), so
  the consolidated outline is closer to the score than the full edge set. Reverted.

- The 200 dpi scan of A104 and A102 attributes their largest blocks to the metric, not to missing geometry. On
  A104 the worst blocks (0 % recall at page 907,389 and 1238,389 pt) contain four vertical board lines; a 1200 dpi
  run-length across them gives 0.085 mm for the reference and 0.106 mm for us, so the widths agree, and a 500 dpi
  crop of the same area with the same 0.5 mm tolerance finds nothing missing. What differs is the pixel value at
  200 dpi: the reference page carries its linework as an embedded raster that is already dark, while our thinner
  vector lines antialias above the 140 threshold on that grid. The two pages also differ slightly in size — the
  reference is 839.98 × 593.98 mm against our 841 × 594 mm — but both rasterise at 7.87 px/mm, so the content does
  not drift. On A102 the largest persistent block (690–780 × 790–850 pt) is a fixture's curved symbolic profile
  (a basin outline) that we do not draw; the room's stipple fill around it matches. Symbolic family curves remain
  outside the decoded subset.

- Re-baselining at the reference's own raster resolution invalidates the high-dpi recall figure. The reference
  pages carry their drawing as 81 tiles of 1063 × 876 px over 238 × 210 pt, i.e. a 321 dpi raster. Scoring at
  320 dpi with the 0.5 mm radius gives precision 96.4–99.8 % on every page but recall 99.0/27.7/97.8/40.4/12.9/99.2 %
  — pages A101, A103 and A104 collapse while A001, A102 and A105 stay at 98–99 %. The cause is the scorer's
  stroke extraction: `line_ink` keeps pixels that survive a 3 × 3 erosion, and our 0.1 mm vector lines are 1.3 px
  at 320 dpi, so erosion removes them, while the reference's raster lines (0.085 mm drawn, ~2 px of antialiased
  dark) survive. Our precision stays high because what little ink we produce lies on the reference's strokes. The
  100 dpi grid, where both sides alias to the same pixel scale, remains the only fair comparison; higher-dpi
  recall measures line width, not missing geometry.

- The A102 block is a door's swing, not a fixture curve. The crop at 690–780 × 790–850 pt shows door 106A: the
  reference draws the leaf and a double fan of swing arcs, our plan draws only the opening. Instrumenting
  `CollectLevel` shows the Level 1 plan (level 311, view 312) has no open door gaps but three saved door plan
  symbols, with 5, 5 and 9 curves and 1, 1 and 2 arcs; the third (a double door) carries the arcs the block
  needs. The symbols are selected and `DrawDoorPlanSymbol` is called for all three, so the swing is lost inside
  that drawing routine — it maps each arc through the symbol matrix and calls `page.DrawArc` with
  `angle ± curve.T0/T1`. Checking that angle convention against the stored arc spans is the next step, not a new
  decode path.

- The swing convention is correct; the missing swing belongs to a generated plan. Instrumenting
  `DrawDoorPlanSymbol` shows thirteen arcs drawn with sane geometry (radius 2.36–2.84 ft, angles −94…−270°), and a
  side-by-side crop of the upper A102 plan shows the Bath and Laundry doors with the same double fan the
  reference draws. The `106A` door sits on the deck plan, which `CollectLevel` builds for level 515270 with no
  view id — its log shows `gaps=0 symbols=0 walls=1`, so no door symbols are selected for it at all. The fix is
  to select the door symbols for a generated plan by level rather than by view id, not to change the arc angles.

- A105's East-elevation guardrail is not missing; the bars are drawn and the 200 dpi gap is sampling. Instrumenting
  the drawn edges in the band 1318–1382 × 288–475 pt names element `933840` (the `SH_1100mm` railing) with 21
  edges there: eight horizontal bars at page y 115–122 mm spanning x 103–175 mm at depth −13054, verticals at
  −7086 and −13064. A 500 dpi crop of the same band with the 0.5 mm tolerance leaves 33 of 44 776 reference
  stroke pixels unmatched (0.1 %) and no extra ink, so the whole guardrail is present. The 35–55 % recall the
  100/200 dpi grids report for those blocks is the thin-line sampling of the reference's embedded raster, the
  same effect documented for A104's boards.


| Sheet | Printed | Still different |
|---|---|---|
| A001 Title Sheet | the title text in its stored frame, the five renderings at their stored sizes, the title-block logo, the "How do I" table with its saved title and instruction-band styles, inline help marker, header and 40 evaluated rows at its instance origin, and every revision table with its header and empty body grid to its fixed height | remaining font and baseline differences in schedule text |
| A101 Site Plan | saved primary and secondary terrain contours and labels, property boundaries with fitted dashes and segment tags, roof surfaces with their saved standing seams and skylight openings, cast shadows on the visible terrain and the saved ground plane for supported lighting, planting at saved instance positions, planting tags with their family frames and leaders, the native north marker, the planting schedule at its instance origin and the Solar Analysis viewport title and the model projected with its saved camera and section box, or the complete rendered study when supplied with `--viewport-image`. The site is projected onto the stored rotated view basis and clipped at its saved crop. | detailed planting symbols, other roof surface patterns, setbacks and spot elevations |
| A102 Plans | both plans with solid background room colours from each view's RVT room-name scheme, the room fills' floor surface patterns (`Sand` in the living room, from the carpet material of the sunken floor type's top compound layer, decoded to its line families and printed as drafting-pattern dashes), furniture and fixtures within the saved view range, including the sunken living room, with white hidden-line surfaces from the projected face rings and a silhouette stroke on a 5 mm grid, saved stair treads, cut marks, owned top-rail projections and supported descending paths, plus horizontal tread outlines where the saved plan shape is unavailable, the observation deck's top rails from the view range (the rails stand on the roof below Level 2; the decoded PlanViewRange2 top and bottom clips decide which are visible), horizontal local door and window tags without leaders from their saved anchors and annotation families, the room legend, horizontal local room tags with saved positions, family labels and line frames, and supported single-label leaders, dashed grid centrelines with their stored bubble visibility and leaders, and the visible section marks and the saved circular elevation markers with their sheet references; four continuous dimension chains from the saved linear dimension curves and witness points, using the project length format; seven horizontal text notes with their saved frames and curved leaders | other styles of view references; furniture family symbolic curves and interior edges of overlapping projected geometry; other dimension styles and rotated or rich-text note layouts are outside the supported subset |
| A103 Elevations/Sections | South elevation and the Longitudinal Section cut at its stored plane, including the six saved level heads and their leaders on the section and the faded site; stacked wall layers hide on the silhouette, coincident modeling inks once, coplanar triangulation diagonals not inked, crop-filtered at generation; every visible edge inks, horizontals included — the wall top, the slab and roof outlines, the sills and the ground line all print, as the reference page shows (its drawing on this sheet travels as a raster image, so the vector layer alone lacks those horizontals and must not be used as the target); a section keeps both its cut profile (the roof slab at z=6898/7178) and the projected horizontals behind the plane, and carves the cross-section out of every solid the plane passes through (wall ends, slab edges, jambs); work below the lowest decoded level prints too (the foundation walls and their hatch down to z=-6302); level lines follow their datum envelopes near the crop with a label-margin cap for narrow viewports, and the grids dash coarsely ([36 9 9 9] pt from the grid type's line pattern), as the reference does | remaining footing and grade edges and detail-specific representations. Some projected interior edges and site shading still differ from the reference |
| A104 Elev/Sec/Det | West and Building Section, four construction details at their stored viewport positions, level and grid heads, nested callout boundaries and sheet references, roof batt insulation with authored breaks and masks, drafting hatch in matching cut material layers | other annotation families, some cut and projected material graphics, and the remaining in-detail 2D geometry |
| A105 Elev/Stair Sections | Stair Section including the surrounding building cut at its stored plane and crop with restored level lines, Main Stair Detail on the file's own crop frame with grid bubbles and the three in-crop level bands at the reference heights, Stair Section grid heads from their full saved envelope with annotation crop disabled, East elevation, hidden lines removed; a railing's continuous rails print as the long diagonals they project across the stair, as the reference page shows, and its balusters stay; the crop filter runs in the true slice (origin-relative rectangle offset by the view origin); cut edges ink with their own category's pen; treads and risers included (they are `OST_StairsTrisers` and their mass hides the stair-body projection edges the reference hides) | the fine stair-profile linework the reference draws as coarse-detail graphics (this page's drawing is raster, so the vector comparison cannot judge it) and the level-line ends at the reference's own markers (bubble positions not decoded) |
