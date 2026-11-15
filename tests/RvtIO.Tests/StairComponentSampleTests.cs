using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// The runs and the landing of the stair sample in three releases (Test_2020, Test_2022, Test_2025: the same project saved by Revit 2020,
/// 2022 and 2025). The elements are found by their element id (the IFC Tag), never by a name. What is asserted is what the type association
/// decides: the type the product names, its property sets and its quantities. Skipped without the sample files.
/// </summary>
public class StairComponentSampleTests
{
    // 513309 and 725046 are monolithic runs (190mm Depth), 665560 an assembled run (50mm Tread 13mm Riser), 725396 a landing (220mm Thickness)
    private static readonly (int Id, string Type, string PredefinedType)[] Runs =
        [(513309, "190mm Depth", ".STRAIGHT."), (665560, "50mm Tread 13mm Riser", ".FREEFORM."), (725046, "190mm Depth", ".STRAIGHT.")];

    private static readonly ConcurrentDictionary<string, Lazy<IfcFile>> Cache = new();

    [SkippableTheory]
    [InlineData("Test_2020.rvt")] [InlineData("Test_2022.rvt")] [InlineData("Test_2025.rvt")] [InlineData("rac_basic_sample_project.rvt")]
    public void EveryRunNamesItsTypeAndCarriesTheRunPropertySets(string sample)
    {
        var ifc = Load(sample);
        foreach (var (id, type, _) in Runs)
        {
            var flight = ifc.Product("IFCSTAIRFLIGHT", id);
            Assert.EndsWith(":" + type, flight.ObjectType);
            Assert.DoesNotContain("Element ", flight.ObjectType);
            var common = Assert.Single(ifc.PropertySetsOf(flight.EntityId), set => set.Name == "Pset_StairFlightCommon");
            Assert.Equal(["Reference", "NumberOfRiser", "NumberOfTreads", "RiserHeight", "TreadLength", "NosingLength", "WalkingLineOffset", "TreadLengthAtOffset", "TreadLengthAtInnerSide"],
                common.Properties);
            var takeOff = Assert.Single(ifc.PropertySetsOf(flight.EntityId), set => set.Name == "Pset_QuantityTakeOff");
            Assert.Equal(["Reference"], takeOff.Properties);
            var flightType = ifc.TypeOf(flight.EntityId);
            Assert.Equal("IFCSTAIRFLIGHTTYPE", flightType.Class);
            Assert.Equal(type, flightType.Name);
        }
    }

    [SkippableTheory]
    [InlineData("Test_2020.rvt")] [InlineData("Test_2022.rvt")] [InlineData("Test_2025.rvt")] [InlineData("rac_basic_sample_project.rvt")]
    public void TheRunTypeKindDecidesThePredefinedTypeAndTheLengthQuantity(string sample)
    {
        var ifc = Load(sample);
        foreach (var (id, _, predefined) in Runs)
        {
            var flight = ifc.Product("IFCSTAIRFLIGHT", id);
            Assert.Equal(predefined, ifc.TypeOf(flight.EntityId).Predefined);
            var quantities = Assert.Single(ifc.QuantitySetsOf(flight.EntityId), set => set.Name == "BaseQuantities");
            // a monolithic run counts its treads along the walking line; an assembled run has no such length. The Length surrogate keeps the
            // inputs it had before the type was resolved (IsRunLengthRuleCurrent) and needs the class root counted once, which every save
            // read by its framing does (900 mm in each, as the reference writes for Test_2020 and Test_2022).
            bool hasLength = id != 665560;
            Assert.Equal(hasLength ? ["Length", "NetVolume"] : ["NetVolume"], quantities.Properties.Order().ToArray());
        }
    }

    [SkippableTheory]
    [InlineData("Test_2020.rvt")] [InlineData("Test_2022.rvt")] [InlineData("Test_2025.rvt")] [InlineData("rac_basic_sample_project.rvt")]
    public void TheLandingNamesItsTypeAloneAndCarriesTheSlabPropertySets(string sample)
    {
        var ifc = Load(sample);
        var landing = ifc.Product("IFCSLAB", 725396);
        Assert.Equal("220mm Thickness", landing.ObjectType);
        var sets = ifc.PropertySetsOf(landing.EntityId).Select(set => set.Name).Order().ToArray();
        Assert.Equal(["Pset_QuantityTakeOff", "Pset_ReinforcementBarPitchOfSlab", "Pset_SlabCommon"], sets);
        Assert.Equal("220mm Thickness", ifc.TypeOf(landing.EntityId).Name);
    }

    /// <summary>The stair of the monolithic runs is straight; the stair of the one sketched run (FREEFORM flight type) is not classified.</summary>
    [SkippableTheory]
    [InlineData("Test_2022.rvt")] [InlineData("Test_2025.rvt")]
    public void AStairWithASketchedRunStaysUndefined(string sample)
    {
        var ifc = Load(sample);
        Assert.Equal(".STRAIGHT_RUN_STAIR.", ifc.Product("IFCSTAIR", 513254).Last);
        Assert.Equal(".NOTDEFINED.", ifc.Product("IFCSTAIR", 665557).Last);
    }

    /// <summary>The same stair with its straight run turned into a sketched one: the current profile must leave it undefined (this is the
    /// case that fails when the profile flag is not handed to the shape rule).</summary>
    [SkippableFact]
    public void ASketchedRunTurnsTheStairUndefinedThroughTheExporter()
    {
        var ifc = Export("Test_2022.rvt", info =>
        {
            var run = info.Elements.Single(e => e.Id == 513309);
            run.SerializedStairRunInput = run.SerializedStairRunInput! with { Style = RvtIO.Parsing.TfBlockDeserializer.StairRunStyle.Sketched };
        });
        Assert.Equal(".NOTDEFINED.", ifc.Product("IFCSTAIR", 513254).Last);
    }

    /// <summary>The files saved by Revit 2023 and later carry no legacy name for a run or a landing (the basic architecture sample of those releases: the
    /// element has a TypeId and nothing else). Test_2025 still has one, so the names are taken away before the export.</summary>
    [SkippableFact]
    public void ARunWithoutAnOwnNameStillTakesItsTypeAndItsPropertySets()
    {
        var ifc = Export("Test_2025.rvt", info =>
        {
            foreach (var element in info.Elements.Where(e => e.CategoryId is -2000919 or -2000920 && e.TypeId > 0 && e.Id is 513309 or 665560 or 725046 or 725396))
                element.Name = null;
        });
        foreach (var (id, type, _) in Runs)
        {
            var flight = ifc.Product("IFCSTAIRFLIGHT", id);
            Assert.EndsWith(":" + type, flight.ObjectType);
            Assert.Contains(ifc.PropertySetsOf(flight.EntityId), set => set.Name == "Pset_QuantityTakeOff" && set.Properties.SequenceEqual(["Reference"]));
            Assert.Contains(ifc.PropertySetsOf(flight.EntityId), set => set.Name == "Pset_StairFlightCommon");
        }
        var landing = ifc.Product("IFCSLAB", 725396);
        Assert.Equal("220mm Thickness", landing.ObjectType);
        Assert.Contains(ifc.PropertySetsOf(landing.EntityId), set => set.Name == "Pset_SlabCommon");
    }

    // ---- reading the written IFC back ----

    private static IfcFile Load(string sample) => Cache.GetOrAdd(sample, name => new Lazy<IfcFile>(() => Export(name))).Value;

    private static IfcFile Export(string sample, Action<RevitFileInfo>? change = null)
    {
        string path = TestFiles.Find(sample);
        var info = new RvtFileReader(new RvtReaderOptions
        {
            R22Layout = true, SystemFamilyNameLocale = "ENU", SeparateFacesWithDistinctOpenings = true,
        }).Read(path);
        change?.Invoke(info);
        string output = Path.Combine(Path.GetTempPath(), $"rvtio-stair-sample-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = IfcProfile.Current }.Export(info, output);
            return new IfcFile(File.ReadAllLines(output));
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    internal sealed record Product(int EntityId, string Name, string ObjectType, string Last);
    internal sealed record PropertySet(string Name, string[] Properties);
    internal sealed record TypeObject(string Class, string Name, string Predefined);

    internal sealed class IfcFile
    {
        private readonly Dictionary<int, (string Type, string[] Args)> _entities = new();
        private readonly Dictionary<int, List<int>> _definedBy = new(), _typeOf = new();

        internal IfcFile(IEnumerable<string> lines)
        {
            foreach (string line in lines)
            {
                var match = Regex.Match(line, @"^#(\d+)=([A-Z0-9_]+)\((.*)\);$");
                if (match.Success) _entities[int.Parse(match.Groups[1].Value)] = (match.Groups[2].Value, IfcExporter.SplitStepArguments(match.Groups[3].Value));
            }
            foreach (var (_, (type, args)) in _entities)
            {
                if (type == "IFCRELDEFINESBYPROPERTIES")
                    foreach (int owner in Refs(args[4])) Add(_definedBy, owner, Refs(args[5]).Single());
                else if (type == "IFCRELDEFINESBYTYPE")
                    foreach (int owner in Refs(args[4])) Add(_typeOf, owner, Refs(args[5]).Single());
            }
        }

        internal Product Product(string ifcClass, int elementId)
        {
            foreach (var (id, (type, args)) in _entities)
                if (type == ifcClass && args.Length > 7 && args[7] == $"'{elementId}'")
                    return new Product(id, Text(args[2]), Text(args[4]), args[^1]);
            throw new Xunit.Sdk.XunitException($"no {ifcClass} with the tag {elementId}");
        }

        internal TypeObject TypeOf(int entityId)
        {
            var (type, args) = _entities[_typeOf[entityId].Single()];
            return new TypeObject(type, Text(args[2]), args[^1]);
        }

        internal IEnumerable<PropertySet> PropertySetsOf(int entityId) => SetsOf(entityId, "IFCPROPERTYSET", 4);
        internal IEnumerable<PropertySet> QuantitySetsOf(int entityId) => SetsOf(entityId, "IFCELEMENTQUANTITY", 5);

        private IEnumerable<PropertySet> SetsOf(int entityId, string setType, int listIndex)
        {
            foreach (int set in _definedBy.GetValueOrDefault(entityId) ?? [])
            {
                var (type, args) = _entities[set];
                if (type == setType)
                    yield return new PropertySet(Text(args[2]), Refs(args[listIndex]).Select(id => Text(_entities[id].Args[0])).ToArray());
            }
        }

        private static IEnumerable<int> Refs(string list) => Regex.Matches(list, @"#(\d+)").Select(m => int.Parse(m.Groups[1].Value));
        private static string Text(string argument) => argument.Length >= 2 && argument[0] == '\'' ? argument[1..^1].Replace("''", "'") : "";
        private static void Add(Dictionary<int, List<int>> map, int key, int value)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(value);
        }
    }
}
