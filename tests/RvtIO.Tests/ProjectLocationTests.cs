using System.Globalization;
using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The active project location (ActiveGeoLocationTrackingElement -> GeoLocation Trf -> GeoSite), the exact
/// source of the IFC site placement, latitude and longitude. The block fixtures are verbatim blocks of
/// sample project B (identity rotation, GeoLocation 1284, GeoSite 1283, tracker 1282) and Test_2022.rvt
/// (GeoLocation 21748 rotated by 37 degrees).</summary>
public class ProjectLocationTests
{
    private const string SsActiveGeoLocation =
        "0405000014442A810001000016080000000000000000000000000000000000000000FFFFFFFF27080000000000000000"
        + "0100000004050000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF000000FFFFFFFFBE08000000"
        + "0011000000530061006D0070006C0065005F00700072006F006A006500630074005F0042003100FFFFFFFFFFFFFFFF00"
        + "01000000FFFFFFFF000000000000000000000000000000000000F03F0000000000000000000000000000000000000000"
        + "00000000000000000000F03F000000000000000000000000000000000000000000000000000000000000F03FC04D1F4C"
        + "42FA14C183EA9605EF4F11C1B933EFCC3BF327C0030500000100000000010000";

    private const string SsTrackingElement =
        "02050000851CDDF251000000830000000000000000000000000000000000000000000000000000000000000000000100"
        + "000002050000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF0000000405000007050000510000"
        + "00";

    private const string SsGeoSite =
        "03050000148C04DE1D020000170800000000000000000000000000000000000000000000000000000000000000000100"
        + "000003050000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF000000FFFFFFFF410FFFFFFFFFFF"
        + "FFFFFFE1EDCB0EFD22EF3FE2A3623E3F0EE53F00000000000014C0000000000040634000000000003070400000000000"
        + "00F03F000000006663EB6365744047BC05196BDAE02397FFFFFFFF00000000000000000E0000001C043E0441043A0432"
        + "0430042C00200020043E044104410438044F040A000000350032003900330039005F0032003000300034000500000030"
        + "0030003000300030000C0000000000000000D071400000000000F0714000000000004072400000000000A07240000000"
        + "00000073400000000000207340000000000030734000000000002073400000000000F072400000000000A07240000000"
        + "000060724000000000001072400C0000000000000000C071400000000000C071400000000000E0714000000000002072"
        + "40000000000070724000000000009072400000000000B072400000000000B07240000000000090724000000000005072"
        + "4000000000002072400000000000F071400C0000003333333333437040DF4F8D976E4C7040DF4F8D976E4C7040DF4F8D"
        + "976E4C7040E17A14AE47557040CDCCCCCCCC5C7040E17A14AE47557040DF4F8D976E4C7040E17A14AE47557040E17A14"
        + "AE47557040DF4F8D976E4C70403333333333437040000000001D020000";

    private const string SampleActiveGeoLocation =
        "F45400005954BE6D0201000016080000000000000000000000000000000000000000FFFFFFFF270800000000FFFFFFFF"
        + "8D0201000000F4540000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF000000FFFFFFFFBE0800"
        + "0000000800000049006E007400650072006E0061006C0000000000D20000000001000000FFFFFFFF0000000000000000"
        + "0000000001000000FFFFFFFFF001FD79A20E6C8EE93FD3BE55941142E33F0000000000000000D3BE55941142E3BFFD79"
        + "A20E6C8EE93F000000000000000000000000000000000000000000000000000000000000F03FCCD3CF75FC445640A891"
        + "A4F9F44724400000000000000000F354000001000000000000000000000002010000";

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    private static uint DataSize(byte[] block) => BitConverter.ToUInt32(block, 8);

    // Values read from the blocks above (feet), written with the round-trip format.
    private const double SsOriginX = -343696.5743381642, SsOriginY = -283643.7554585116, SsOriginZ = -11.975065616797893;
    private const double SampleCos = 0.7986355100472903, SampleSin = 0.6018150231520515;
    private const double SampleOriginX = 89.07790894790304, SampleOriginY = 10.140540887202249;


    [Fact]
    public void GeoLocation_IdentityRotationAndOriginAndSymbolAreRead()
    {
        byte[] block = Bytes(SsActiveGeoLocation);

        var location = TfBlockDeserializer.ExtractGeoLocation(block, 0, DataSize(block), 2070);

        Assert.NotNull(location);
        Assert.Equal([1.0, 0, 0, 0, 1.0, 0, 0, 0, 1.0], location!.Value.Rotation);
        Assert.Equal([SsOriginX, SsOriginY, SsOriginZ], location.Value.Origin);
        Assert.Equal(1283, location.Value.SymbolId);
    }

    [Fact]
    public void GeoLocation_RotatedMatrixKeepsRowsAndOrigin()
    {
        byte[] block = Bytes(SampleActiveGeoLocation);

        var location = TfBlockDeserializer.ExtractGeoLocation(block, 0, DataSize(block), 2070);

        Assert.NotNull(location);
        Assert.Equal([SampleCos, SampleSin, 0, -SampleSin, SampleCos, 0, 0, 0, 1.0], location!.Value.Rotation);
        Assert.Equal([SampleOriginX, SampleOriginY, 0], location.Value.Origin);
        Assert.Equal(21747, location.Value.SymbolId);
    }

    [Theory]
    [InlineData("class")] [InlineData("footer")] [InlineData("truncated")] [InlineData("size")]
    public void GeoLocation_NotAValidatedBlockIsRejected(string mutation)
    {
        byte[] block = Bytes(SsActiveGeoLocation);
        uint size = DataSize(block);
        ushort cls = 2070;
        switch (mutation)
        {
            case "class": cls = 2071; break;
            case "footer": block[^1] ^= 1; break;
            case "truncated": block = block[..120]; break;
            case "size": size += 4; break;
        }

        Assert.Null(TfBlockDeserializer.ExtractGeoLocation(block, 0, size, cls));
    }

    [Fact]
    public void TrackingElement_NamesTheActiveLocation()
    {
        byte[] block = Bytes(SsTrackingElement);

        var parameters = TfBlockDeserializer.ExtractParams(block, 0, DataSize(block), 131);

        Assert.NotNull(parameters);
        Assert.Equal(1284, parameters!.Fields["m_activeGeoLocationId"]);
    }

    [Fact]
    public void GeoSite_LatitudeAndLongitudeAreRadians()
    {
        byte[] block = Bytes(SsGeoSite);

        var parameters = TfBlockDeserializer.ExtractParams(block, 0, DataSize(block), 2071);

        Assert.NotNull(parameters);
        Assert.Equal(0.9730210579868378, parameters!.DoubleFields["m_dLatitude"]);
        Assert.Equal(0.6579891413176677, parameters.DoubleFields["m_dLongitude"]);
    }

    // The later block of an element is the current one.

    [Fact]
    public void Record_StoresTheThreeKindsAndALaterBlockReplacesAnEarlierOne()
    {
        var state = new ParsingState();
        byte[] location = Bytes(SsActiveGeoLocation);
        byte[] tracker = Bytes(SsTrackingElement);
        byte[] site = Bytes(SsGeoSite);
        RvtFileReader.RecordProjectLocation(state, tracker, 0, DataSize(tracker), 131, 1282);
        RvtFileReader.RecordProjectLocation(state, location, 0, DataSize(location), 2070, 1284);
        RvtFileReader.RecordProjectLocation(state, site, 0, DataSize(site), 2071, 1283);

        Assert.Equal(1284u, state.ActiveGeoLocations[1282]);
        Assert.Equal(1283, state.GeoLocations[1284].SymbolId);
        Assert.Equal((0.9730210579868378, 0.6579891413176677), state.GeoSites[1283]);

        // The rotated block saved later under the same element id replaces the first one.
        byte[] later = Bytes(SampleActiveGeoLocation);
        BitConverter.GetBytes(1284u).CopyTo(later, 0);
        RvtFileReader.RecordProjectLocation(state, later, 0, DataSize(later), 2070, 1284);

        Assert.Equal(SampleCos, state.GeoLocations[1284].Rotation[0]);
        Assert.Single(state.GeoLocations);
    }

    [Fact]
    public void Record_IgnoresABlockWhoseHeaderNamesAnotherElement()
    {
        var state = new ParsingState();
        byte[] location = Bytes(SsActiveGeoLocation);

        RvtFileReader.RecordProjectLocation(state, location, 0, DataSize(location), 2070, 999);
        RvtFileReader.RecordProjectLocation(state, location, 0, DataSize(location), 480, 1284);

        Assert.Empty(state.GeoLocations);
    }


    private static ParsingState Resolvable(double[] rotation, double[] origin, double surveyShiftFeet = 0,
        bool survey = true, double latitude = 0.9730210579868378)
    {
        var state = new ParsingState();
        state.ActiveGeoLocations[1282] = 1284;
        state.GeoLocations[1284] = (rotation, origin, 1283);
        state.GeoSites[1283] = (latitude, 0.6579891413176677);
        if (!survey) return state;

        // The survey point sits at the shared origin, so its internal position is the Trf origin.
        state.Records[1285] = new ElementRecord { ElemId = 1285, CategoryId = -2001272 };
        state.BasePointCoordinates[1285] = ([origin[0], origin[1], origin[2]], [surveyShiftFeet, 0, 0]);
        state.Records[1288] = new ElementRecord { ElemId = 1288, CategoryId = -2001271 };
        state.BasePointCoordinates[1288] = ([0, 0, 0], [0, 0, 0]);
        return state;
    }

    private static readonly double[] Identity = [1.0, 0, 0, 0, 1.0, 0, 0, 0, 1.0];

    [Fact]
    public void Resolve_IdentityRotationTranslatesByTheNegatedOrigin()
    {
        var state = Resolvable(Identity, [SsOriginX, SsOriginY, SsOriginZ]);
        state.GeoSiteTimeZones[1283] = 5.5;

        var location = RvtFileReader.ResolveActiveProjectLocation(state, null);

        Assert.NotNull(location);
        Assert.Equal([1.0, 0, 0, 0, 1.0, 0, 0, 0, 1.0, 343696.5743381642, 283643.7554585116, 11.975065616797893],
            location!.SharedTransform);
        Assert.Equal(0.9730210579868378, location.Latitude);
        Assert.Equal(0.6579891413176677, location.Longitude);
        Assert.Equal(5.5, location.TimeZoneHours);
        Assert.Equal(11.975065616797893, location.ElevationFeet);
    }

    [Fact]
    public void Resolve_RotatedLocationMatchesTheReferenceSiteOfTest2022()
    {
        var state = Resolvable([SampleCos, SampleSin, 0, -SampleSin, SampleCos, 0, 0, 0, 1.0], [SampleOriginX, SampleOriginY, 0],
            survey: false);

        var location = RvtFileReader.ResolveActiveProjectLocation(state, null);

        Assert.NotNull(location);
        var t = location!.SharedTransform;
        Assert.Equal(SampleCos, t[0]);
        Assert.Equal(SampleSin, t[1]);
        Assert.Equal(-SampleSin, t[3]);
        // The expected export site origin of Test_2022 (millimetres): -19823.598066033803, -18808.299659248678.
        Assert.Equal(-19823.598066033803, t[9] * 304.8, 9);
        Assert.Equal(-18808.299659248678, t[10] * 304.8, 9);
        Assert.False(double.IsNegative(t[11]));
        Assert.Equal(0, location.ElevationFeet);
    }

    [Fact]
    public void Resolve_WithoutAnActiveLocationRecordNothingIsResolved()
    {
        Assert.Null(RvtFileReader.ResolveActiveProjectLocation(new ParsingState(), null));
    }

    [Fact]
    public void Resolve_TwoTrackingElementsAreAmbiguous()
    {
        var state = Resolvable(Identity, [SsOriginX, SsOriginY, SsOriginZ]);
        state.ActiveGeoLocations[5000] = 1284;

        Assert.Null(RvtFileReader.ResolveActiveProjectLocation(state, null));
    }

    [Fact]
    public void Resolve_ActiveLocationWithoutADecodedBlockIsUnresolved()
    {
        var state = Resolvable(Identity, [SsOriginX, SsOriginY, SsOriginZ]);
        state.ActiveGeoLocations[1282] = 7777;

        Assert.Null(RvtFileReader.ResolveActiveProjectLocation(state, null));
    }

    [Theory]
    [InlineData(2)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void Resolve_ATrfThatIsNotARotationAboutZIsRejected(int index)
    {
        var matrix = (double[])Identity.Clone();
        matrix[index] = index == 8 ? 0.5 : 0.1;
        var state = Resolvable(matrix, [SsOriginX, SsOriginY, SsOriginZ]);

        Assert.Null(RvtFileReader.ResolveActiveProjectLocation(state, null));
    }

    [Fact]
    public void Resolve_NonFiniteOriginIsRejected()
    {
        var state = Resolvable(Identity, [double.NaN, SsOriginY, SsOriginZ], survey: false);

        Assert.Null(RvtFileReader.ResolveActiveProjectLocation(state, null));
    }

    [Fact]
    public void Resolve_SurveyPointThatDisagreesWithTheTransformRejectsIt()
    {
        var state = Resolvable(Identity, [SsOriginX, SsOriginY, SsOriginZ], surveyShiftFeet: 0.01);

        Assert.Null(RvtFileReader.ResolveActiveProjectLocation(state, null));
    }

    [Fact]
    public void Resolve_LatitudeOutsideTheGlobeDropsOnlyTheCoordinates()
    {
        var state = Resolvable(Identity, [SsOriginX, SsOriginY, SsOriginZ], latitude: 100.0);

        var location = RvtFileReader.ResolveActiveProjectLocation(state, null);

        Assert.NotNull(location);
        Assert.Null(location!.Latitude);
        Assert.Null(location.Longitude);
        Assert.Equal(343696.5743381642, location.SharedTransform[9]);
    }

    [Fact]
    public void Resolve_NoProjectBasePointMeansZeroElevation()
    {
        var state = Resolvable(Identity, [SsOriginX, SsOriginY, SsOriginZ]);
        state.BasePointCoordinates.Remove(1288);

        Assert.Equal(0, RvtFileReader.ResolveActiveProjectLocation(state, null)!.ElevationFeet);
    }

    [Fact]
    public void Resolve_ProjectBasePointAboveTheOriginAddsItsInternalHeight()
    {
        var state = Resolvable(Identity, [SsOriginX, SsOriginY, SsOriginZ]);
        state.BasePointCoordinates[1288] = ([0, 0, 2.0], [0, 0, 0]);

        Assert.Equal(2.0 + 11.975065616797893,
            RvtFileReader.ResolveActiveProjectLocation(state, null)!.ElevationFeet, 12);
    }
}
