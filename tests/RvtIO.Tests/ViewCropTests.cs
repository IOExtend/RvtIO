using System.Buffers.Binary;
using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Viewport crop boxes (BlockParser.CollectViewCrops), their join to views
/// (ElementBuilder.ApplyViewCrops) and the sheet frame math (SheetComposer.TryCropFrame):
/// the crop rectangle a viewport draws at the view's own scale (rvt-prt's to_sheet).</summary>
public class ViewCropTests
{
    private static byte[] ChunkWithViewerBlock(uint eid, double[] origin, double[][] basis,
        double[] offsets, byte[] flags, int viewId = 312)
    {
        var payload = new List<byte>();
        payload.AddRange(new byte[64]);
        foreach (double v in origin) payload.AddRange(BitConverter.GetBytes(v));
        foreach (var row in basis) foreach (double v in row) payload.AddRange(BitConverter.GetBytes(v));
        foreach (double v in offsets) payload.AddRange(BitConverter.GetBytes(v));
        payload.AddRange(flags);
        // The viewed element id 62 bytes past the end of the bounded space.
        payload.AddRange(new byte[62]);
        payload.AddRange(BitConverter.GetBytes(viewId));
        payload.AddRange(new byte[64]);
        var chunk = new byte[16 + payload.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk, eid);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(8), payload.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(12), 0x000005BF);
        payload.ToArray().CopyTo(chunk, 16);
        return chunk;
    }

    private static readonly double[] Origin = [0, 0, 0];
    private static readonly double[][] Basis = [[1, 0, 0], [0, 1, 0], [0, 0, -1]];
    private static readonly double[] Offsets = [74.2701626513909, -39.85500265497097, 22.32640997306359, -63.25313010110109, 100, -100];
    private static readonly byte[] Flags = [1, 1, 1, 1, 0, 0, 1];

    [Fact]
    public void CollectViewCrops_ReadsCropBoxOfViewerBlock()
    {
        var state = new ParsingState();

        BlockParser.CollectViewCrops(ChunkWithViewerBlock(313, Origin, Basis, Offsets, Flags), state);

        var crop = Assert.Contains(313u, state.ViewCropBlocks);
        Assert.Equal([0, 0, 0], crop.Origin);
        Assert.Equal(74.2701626513909, crop.Offsets[0], 9);
        Assert.Equal([true, true, true, true, false, false], crop.BoundActive);
    }

    [Fact]
    public void CollectViewCrops_FindsPatternInAnyBlockClass()
    {
        var chunk = ChunkWithViewerBlock(313, Origin, Basis, Offsets, Flags);
        // Not the viewer class: the pattern, not the file-specific class index, identifies the box.
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(12), 0x00000001);
        var state = new ParsingState();

        BlockParser.CollectViewCrops(chunk, state);

        Assert.Contains(313u, state.ViewCropBlocks);
    }

    [Fact]
    public void CollectViewCrops_RejectsGarbage()
    {
        var chunk = new byte[16 + 160];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk, 313);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(8), 160);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(12), 0x000005BF);
        for (int i = 0; i < 160; i++) chunk[16 + i] = (byte)(i * 7);
        var state = new ParsingState();

        BlockParser.CollectViewCrops(chunk, state);

        Assert.Empty(state.ViewCropBlocks);
    }

    private static (ParsingState State, Element View) JoinedState(int viewId = 312)
    {
        var state = new ParsingState();
        state.ViewCropBlocks[313] = ([0, 0, 10], [[1, 0, 0], [0, 1, 0], [0, 0, -1]],
            [45.0, -20.0, 15.0, -50.0, 100, -100], [true, true, true, true, false, false], viewId);
        var view = new Element { Id = 312 };
        return (state, view);
    }

    [Fact]
    public void ApplyViewCrops_JoinsViewerBoxToItsView()
    {
        var (state, view) = JoinedState();
        var info = new RevitFileInfo { Elements = [view] };

        ElementBuilder.ApplyViewCrops(info, state);

        Assert.NotNull(view.ViewCrop);
        Assert.Equal(-20.0, view.ViewCrop!.X0, 9);
        Assert.Equal(-50.0, view.ViewCrop.Y0, 9);
        Assert.Equal(45.0, view.ViewCrop.X1, 9);
        Assert.Equal(15.0, view.ViewCrop.Y1, 9);
        Assert.True(view.ViewCrop.Active);
        Assert.Equal([1, 0, 0], view.ViewCrop.Right);
        Assert.Equal([0, 1, 0], view.ViewCrop.Up);
    }

    [Fact]
    public void ApplyViewCrops_InactiveBoundsKeepWholeModel()
    {
        // No active side (a crop region switched off): the rectangle spans ±10000 and counts as off.
        var state = new ParsingState();
        state.ViewCropBlocks[313] = ([0, 0, 0], [[1, 0, 0], [0, 1, 0], [0, 0, -1]],
            [10000.0, -10000.0, 10000.0, -10000.0, 100, -100], [false, false, false, false, false, false], 312);
        var view = new Element { Id = 312 };
        var info = new RevitFileInfo { Elements = [view] };

        ElementBuilder.ApplyViewCrops(info, state);

        Assert.NotNull(view.ViewCrop);
        Assert.False(view.ViewCrop!.Active);
    }

    [Fact]
    public void ApplyViewCrops_UnknownViewIsIgnored()
    {
        // The stored viewed id names no view: no frame is attached anywhere.
        var (state, _) = JoinedState(viewId: 999999);
        var view = new Element { Id = 312 };
        var info = new RevitFileInfo { Elements = [view] };

        ElementBuilder.ApplyViewCrops(info, state);

        Assert.Null(view.ViewCrop);
    }

    [Fact]
    public void CropContentRect_OffsetsOriginRelativeCropByViewOrigin()
    {
        // The Main Stair Detail crop from the sample file: origin-relative view feet plus the view
        // origin's projection on the basis rows, in millimetres (probed from rac_basic_sample_project.rvt).
        var crop = new ViewCropFrame(40.5470, -23.9636, 12.1369, [0, 1, 0], [0, 0, 1], [-1, 0, 0],
            11.9093, -12.3500, 35.2041, 2.1365, true);

        var (x0, y0, x1, y1) = SheetComposer.CropContentRect(crop);

        Assert.Equal((-23.9636 + 11.9093) * 304.8, x0, 3);
        Assert.Equal((-23.9636 + 35.2041) * 304.8, x1, 3);
        Assert.Equal((12.1369 - 12.3500) * 304.8, y0, 3);
        Assert.Equal((12.1369 + 2.1365) * 304.8, y1, 3);
    }

    [Fact]
    public void SampleFile_StairDetailClipLandsOnItsFrame()
    {
        // End to end on the sample model: the detail's absolute crop edges map through DrawContent's
        // (content - Min) / scale + origin mapping exactly onto the TryCropFrame frame corners. With the
        // origin-relative rectangle the clip used to miss the frame by origin / scale (365 mm at 1:20).
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        Element? view = null;
        foreach (var e in document.Elements)
            if (e.CategoryId == (int)BuiltInCategory.OST_Views && e.Name == "Main Stair Detail") view = e;
        if (view?.ViewCrop is not { Active: true } crop) throw new SkipException("Main Stair Detail has no crop.");
        Assert.True(SheetComposer.ViewBasisMatches(view, crop));
        Element? viewport = null;
        foreach (var e in document.Elements)
            if (e.CategoryId == (int)BuiltInCategory.OST_Viewports && e.ViewportViewId == view.Id) viewport = e;
        int scale = view.LookupParameter(BuiltInParameter.VIEW_SCALE)?.Value is int value && value > 0
            ? value : viewport?.ViewportScale ?? 100;
        double centerX = viewport?.ViewportOffset is { Length: >= 2 } offset ? offset[0] * 304.8 : 0;
        double centerY = viewport?.ViewportOffset is { Length: >= 2 } off ? off[1] * 304.8 : 0;

        var content = ElevationPrinter.Collect(document, "section", false, view, null);
        Assert.NotNull(content);
        Assert.True(SheetComposer.TryCropFrame(centerX, centerY, scale, crop, content!.MinX, content.MinY,
            out double frameX, out double frameY, out _, out _, out double originX, out double originY));
        var (x0, y0, x1, y1) = SheetComposer.CropContentRect(crop);
        Assert.Equal(frameX, originX + (x0 - content.MinX) / scale, 6);
        Assert.Equal(frameY, originY + (y0 - content.MinY) / scale, 6);
        Assert.Equal(frameX + (x1 - x0) / scale, originX + (x1 - content.MinX) / scale, 6);
    }

    [Fact]
    public void TryCropFrame_MapsCropToSheetAtTrueScale()
    {
        // View 312 on A102: crop -39.855..74.27 x -63.253..22.326 ft at 1:100, viewport centre from its offset.
        var crop = new ViewCropFrame(0, 0, 0, [1, 0, 0], [0, 1, 0], [0, 0, -1],
            -39.855, -63.25313010110109, 74.2701626513909, 22.32640997306359, true);
        double centerX = 0.5920986668670893 * 304.8, centerY = 1.485536794211 * 304.8;
        double contentMinX = -5000, contentMinY = -20000;

        bool ok = SheetComposer.TryCropFrame(centerX, centerY, 100, crop, contentMinX, contentMinY,
            out double frameX, out double frameY, out double frameW, out double frameH,
            out double originX, out double originY);

        Assert.True(ok);
        double k = 304.8 / 100;
        Assert.Equal((74.2701626513909 - -39.855) * k, frameW, 6);
        Assert.Equal((22.32640997306359 - -63.25313010110109) * k, frameH, 6);
        // The crop corner lands on offset + crop / scale ...
        Assert.Equal(centerX + -39.855 * k, frameX, 6);
        Assert.Equal(centerY + -63.25313010110109 * k, frameY, 6);
        // ... and DrawContent's origin maps content millimetres onto the same points.
        double cropModelX = (-39.855 + 0) * 304.8, cropModelY = (-63.25313010110109 + 0) * 304.8;
        Assert.Equal(frameX, originX + (cropModelX - contentMinX) / 100, 6);
        Assert.Equal(frameY, originY + (cropModelY - contentMinY) / 100, 6);
    }

    [Fact]
    public void TryCropFrame_RejectsRotatedBasis()
    {
        var crop = new ViewCropFrame(0, 0, 0, [0.7986, -0.6018, 0], [0.6018, 0.7986, 0], [0, 0, -1],
            -122.0, -196.0, 190.0, 106.0, true);

        bool ok = SheetComposer.TryCropFrame(0, 0, 100, crop, 0, 0,
            out _, out _, out _, out _, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryCropFrame_MapsSectionBasisToSheet()
    {
        // A section looking along +x: right is -y, up is +z; content is already projected on those axes.
        var crop = new ViewCropFrame(10, 20, 5, [0, -1, 0], [0, 0, 1], [1, 0, 0],
            -70, -10, 80, 35, true);

        bool ok = SheetComposer.TryCropFrame(1000, 2000, 100, crop, -50000, -30000,
            out double frameX, out double frameY, out double frameW, out double frameH,
            out double originX, out double originY);

        Assert.True(ok);
        double k = 304.8 / 100;
        Assert.Equal(1000 + -70 * k, frameX, 6);
        Assert.Equal(2000 + -10 * k, frameY, 6);
        Assert.Equal(150 * k, frameW, 6);
        Assert.Equal(45 * k, frameH, 6);
        // The view origin in content units: (origin . right) and (origin . up), in millimetres.
        Assert.Equal(1000 + (-50000 - -20 * 304.8) / 100, originX, 6);
        Assert.Equal(2000 + (-30000 - 5 * 304.8) / 100, originY, 6);
        // The crop corner lands on the frame corner through DrawContent's mapping: the model point
        // at view (X0, Y0) is origin + X0 * right + Y0 * up, and its content coordinates are its dot
        // products with the basis rows.
        double cropModelX = (-70 + (10 * 0 + 20 * -1 + 5 * 0)) * 304.8;
        double cropModelY = (-10 + (10 * 0 + 20 * 0 + 5 * 1)) * 304.8;
        Assert.Equal(frameX, originX + (cropModelX - -50000) / 100, 6);
        Assert.Equal(frameY, originY + (cropModelY - -30000) / 100, 6);
    }

    [Fact]
    public void ViewBasisMatches_AcceptsTheDecodedPlane()
    {
        // View 8237 (a South-ish elevation): the decoded view plane equals the viewer basis.
        var view = new Element
        {
            Id = 8237,
            ViewOrigin = [-34.735, -2.5124, 3.937],
            ViewHorizontalDirection = [0, -1, 0],
            ViewVerticalDirection = [0, 0, 1],
            ViewDirection = [1, 0, 0],
        };
        var crop = new ViewCropFrame(-34.735, -2.5124, 3.937, [0, -1, 0], [0, 0, 1], [1, 0, 0],
            -78.0, -12.0, 73.0, 32.0, true);

        Assert.True(SheetComposer.ViewBasisMatches(view, crop));
    }

    [Fact]
    public void ViewBasisMatches_RejectsMismatchOrMissingPlane()
    {
        var view = new Element
        {
            Id = 8237,
            ViewOrigin = [-34.735, -2.5124, 3.937],
            ViewHorizontalDirection = [0, -1, 0],
            ViewVerticalDirection = [0, 0, 1],
            ViewDirection = [1, 0, 0],
        };
        var shifted = new ViewCropFrame(0, 0, 0, [0, -1, 0], [0, 0, 1], [1, 0, 0],
            -78.0, -12.0, 73.0, 32.0, true);
        var noPlane = new Element { Id = 312 };

        Assert.False(SheetComposer.ViewBasisMatches(view, shifted));
        Assert.False(SheetComposer.ViewBasisMatches(noPlane, shifted));
        Assert.False(SheetComposer.ViewBasisMatches(null, shifted));
    }
}
