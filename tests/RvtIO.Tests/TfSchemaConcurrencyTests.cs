using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class TfSchemaConcurrencyTests
{
    [Fact]
    public async Task ConcurrentColdSchemaReads_ShareOnePublishedSnapshot()
    {
        // A separate collectible context makes this a genuine cold start, without
        // resetting or modifying the default parser's shared state in parallel tests.
        var context = new AssemblyLoadContext(nameof(ConcurrentColdSchemaReads_ShareOnePublishedSnapshot), isCollectible: true);
        try
        {
            Type parser = Parser(context);
            PropertyInfo schemas = parser.GetProperty("Schemas", BindingFlags.Public | BindingFlags.Static)!;
            object[] snapshots = await ColdReads(() => schemas.GetValue(null)!);
            Assert.All(snapshots, snapshot => Assert.True(ReferenceEquals(snapshots[0], snapshot),
                "Concurrent readers received different schema snapshots."));
        }
        finally { context.Unload(); }
    }

    [Fact]
    public async Task ConcurrentColdSerializationChains_KeepEveryDeclaringClass()
    {
        var context = new AssemblyLoadContext(nameof(ConcurrentColdSerializationChains_KeepEveryDeclaringClass), isCollectible: true);
        try
        {
            Type parser = Parser(context);
            MethodInfo fields = parser.GetMethod("SerializationFields", BindingFlags.Public | BindingFlags.Static)!;
            object[] chains = await ColdReads(() => fields.Invoke(null, [646])!);
            foreach (IEnumerable chain in chains)
            {
                int[] declaring = chain.Cast<object>().Select(field =>
                    (int)field.GetType().GetProperty("Declaring")!.GetValue(field)!).ToArray();
                Assert.NotEmpty(declaring);
                Assert.All(declaring, value => Assert.True(value > 0));
                Assert.Contains(37, declaring);
                Assert.Contains(646, declaring);
            }
        }
        finally { context.Unload(); }
    }

    [Fact]
    public void SerializationChainsMatchAllShippedClassBasesAndFields()
    {
        var schemas = TfBlockDeserializer.Schemas;
        foreach (int classId in schemas.Keys)
        {
            var ancestors = new Stack<int>();
            for (int? current = classId; current is int id && schemas.TryGetValue(id, out var schema);
                current = schema.Base)
            {
                Assert.DoesNotContain(id, ancestors);
                ancestors.Push(id);
            }

            var expected = ancestors.SelectMany(id => schemas[id].Fields.Select(field => field with { Declaring = id }));
            Assert.Equal(expected, TfBlockDeserializer.SerializationFields(classId));
            Assert.All(ancestors, baseId => Assert.True(TfBlockDeserializer.IsSubclassOf(classId, baseId)));
            Assert.False(TfBlockDeserializer.IsSubclassOf(classId, int.MaxValue));
        }
        Assert.Null(TfBlockDeserializer.SerializationFields(int.MaxValue));
    }

    [Fact]
    public void NativeStoreRowsMatchTheShippedLayouts()
    {
        (string Name, byte Type, byte Mods, int? Ref) id = ("m_paramId", 14, 0, 20);
        Assert.Equal([id, ("m_value", (byte)8, (byte)96, null)], Layout(2882));
        Assert.Equal([("m_value", (byte)7, (byte)0, null), id], Layout(2883));
        Assert.Equal([id, ("m_value", (byte)14, (byte)0, 20)], Layout(2884));
        Assert.Equal([id, ("m_value", (byte)4, (byte)0, null)], Layout(2885));

        static IEnumerable<(string, byte, byte, int?)> Layout(int classId) =>
            TfBlockDeserializer.SerializationFields(classId)!.Select(field =>
                (field.Name, field.Type, field.Mods, field.Ref));
    }

    [Fact]
    public void FieldCopiesRecomputeCaptureKeysWhenIdentityChanges()
    {
        var field = new TfBlockDeserializer.TfField("m_hostId", 14, 0, 20, null, 1795);
        var equalBeforeCapture = field with { };

        Assert.Equal("1795:m_hostId", field.ProductionCaptureKey);
        Assert.Equal(equalBeforeCapture, field);
        Assert.Equal(equalBeforeCapture.GetHashCode(), field.GetHashCode());
        Assert.Null((field with { Name = "m_uncaptured" }).ProductionCaptureKey);
        var moved = field with { Declaring = 0 };
        Assert.Equal("0:m_hostId", moved.QualifiedKey);
        Assert.Null(moved.ProductionCaptureKey);
    }

    private static Type Parser(AssemblyLoadContext context) => context
        .LoadFromAssemblyPath(typeof(TfBlockDeserializer).Assembly.Location)
        .GetType("RvtIO.Parsing.TfBlockDeserializer", throwOnError: true)!;

    private static async Task<object[]> ColdReads(Func<object> read)
    {
        const int workers = 16;
        using var ready = new CountdownEvent(workers);
        using var start = new ManualResetEventSlim();
        Task<object>[] tasks = Enumerable.Range(0, workers).Select(_ => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            if (!start.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Cold-read start gate timed out.");
            return read();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        bool allReady = ready.Wait(TimeSpan.FromSeconds(20));
        start.Set();
        object[] values = await Task.WhenAll(tasks);
        Assert.True(allReady, "Dedicated readers did not reach the cold-start gate.");
        return values;
    }
}
