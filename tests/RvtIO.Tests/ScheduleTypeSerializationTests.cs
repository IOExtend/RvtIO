using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class ScheduleTypeSerializationTests
{
    private const ushort ScheduleClass = 1012;
    private const int Owner = 100;
    private const int StoredType = 200;
    private const int Viewer = 300;
    private const int Template = 400;

    [Theory]
    [InlineData(0u, StoredType, StoredType)]
    [InlineData(2u, StoredType, StoredType)]
    [InlineData(uint.MaxValue, StoredType, StoredType)]
    [InlineData(2u, -1, Viewer)]
    public void UsesStoredViewTypeThroughTheScheduleColorMap(uint colors, int storedType, int expected)
    {
        byte[] block = Schedule(colors, storedType);
        var parameters = Read(block);
        Assert.NotNull(parameters);
        Assert.Equal(storedType, parameters.Fields["m_dbViewTypeId"]);

        var state = new ParsingState();
        state.NativeParams[Owner] = parameters;
        state.ElemClassIdx[Owner] = ScheduleClass;
        var schedule = new Element { Id = Owner, TypeId = Viewer, CategoryId = -2000573 };
        var info = new RevitFileInfo();
        info.Elements.Add(schedule);

        ElementBuilder.ApplyNativeParams(info, state);

        Assert.Equal((long)expected, schedule.TypeElementId);
    }

    [Fact]
    public void RejectsAColorMapThatExtendsPastTheBlock()
        => Assert.Null(Read(Schedule(uint.MaxValue - 1, StoredType)));

    [Theory]
    [InlineData(StoredType, 1020, StoredType)]
    [InlineData(Template, 1020, Viewer)]
    [InlineData(StoredType, ScheduleClass, Viewer)]
    public void TypesFromFramedCopiesRequireAgreementAndAnOwnViewType(
        int otherCopyType, ushort targetClass, int expected)
    {
        byte[] current = Schedule(2, StoredType);
        byte[] otherCopy = Schedule(2, otherCopyType);
        byte[] target = Block(StoredType, targetClass, 0, -1);
        var state = new ParsingState
        {
            AllKnownIds = [Owner, StoredType],
            ElemRecOwningElementIds = new() { [Owner] = -1, [StoredType] = -1 },
            FramedPartitions =
            [
                new([current], [new(102, Owner, 0, current.Length)], 4, 1),
                new([otherCopy], [new(102, Owner, 0, otherCopy.Length)], 4, 2),
                new([target], [new(102, StoredType, 0, target.Length)], 4, 2),
            ],
        };
        var schedule = new Element { Id = Owner, TypeId = Viewer, CategoryId = -2000573 };
        RvtFileReader.RunTfStreamWalk(state);
        ElementBuilder.ApplyNativeParams(new RevitFileInfo { Elements = [schedule] }, state);

        Assert.Equal((long)expected, schedule.TypeElementId);
    }

    private static TfBlockDeserializer.NativeParams? Read(byte[] block)
        => TfBlockDeserializer.ExtractParams(block, 0, BitConverter.ToUInt32(block, 8), ScheduleClass);

    private static byte[] Schedule(uint colors, int storedType)
        => Block(Owner, ScheduleClass, colors, storedType);

    private static byte[] Block(int owner, ushort classId, uint colors, int storedType)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(owner);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(classId);
        foreach (var field in TfBlockDeserializer.SerializationFields(classId)!)
        {
            switch (field.Name)
            {
                case "m_docAccess": writer.Write(1u); break;
                case "m_id": writer.Write(owner); break;
                case "m_dbViewTypeId": writer.Write(storedType); break;
                case "m_viewerId": writer.Write(Viewer); break;
                case "m_viewTemplateId": writer.Write(Template); break;
                case "m_colorToCategoryIdMap":
                    writer.Write(colors);
                    if (colors == 2)
                    {
                        writer.Write(0x004080C0u);
                        writer.Write(700);
                        writer.Write(0x005090D0u);
                        writer.Write(701);
                    }
                    break;
                case "m_fontIds":
                    writer.Write(1u);
                    writer.Write(500);
                    break;
                default:
                    int bytes = field.Type switch
                    {
                        1 => 1,
                        7 => 8,
                        4 or 8 or 14 => 4,
                        _ => throw new InvalidOperationException(field.Name)
                    };
                    writer.Write(new byte[bytes * (field.Cnt ?? 1)]);
                    break;
            }
        }
        uint size = checked((uint)stream.Length - 12);
        writer.Write(size);
        stream.Position = 8;
        writer.Write(size);
        return stream.ToArray();
    }
}
