using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

public sealed class DocumentIncrementFixupTests
{
    [Fact]
    public void RefreshTotals_SetsLastLocalIncrementToActualCount()
    {
        const string path = @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt";
        using var root = RootStorage.OpenRead(path);
        var doc = TfSchemaReader.Read(root)!;
        var data = PagedGzipStream.Read(root, "Global", "DocumentIncrementTable")!;
        var patched = DocumentIncrementFixup.RefreshTotals(data, doc, 567684);

        // verify by manual navigation: last local increment and its (-1) version
        var tableFields = PayloadSpan.Fields(patched, 2, patched.Length, doc,
            doc.Classes.First(c => c.Name == "DocumentIncrementTable").Id);
        var local = tableFields.First(f => f.Name == "m_localIncrements");
        int itemCount = BitConverter.ToInt32(patched, local.Start);
        int incrementId = doc.Classes.First(c => c.Name == "DocumentIncrement").Id;
        int versionId = doc.Classes.First(c => c.Name == "DocumentIncrementVersion").Id;
        int at = local.Start + 4;
        (int Start, int End) lastItem = default;
        for (int i = 0; i < itemCount; i++)
        {
            var fields = PayloadSpan.Fields(patched, at, local.End, doc, incrementId);
            Assert.NotEmpty(fields);
            lastItem = (at, fields[^1].End);
            at = lastItem.End;
        }
        var lastFields = PayloadSpan.Fields(patched, lastItem.Start, lastItem.End, doc, incrementId);
        var totalField = lastFields.First(f => f.Name == "m_totalElements");
        Assert.Equal(567684u, BitConverter.ToUInt32(patched, totalField.Start));
        var versionsField = lastFields.First(f => f.Name == "m_incrementVersions");
        int versionCount = BitConverter.ToInt32(patched, versionsField.Start);
        int vat = versionsField.Start + 4;
        int foundMinusOne = 0;
        for (int i = 0; i < versionCount; i++)
        {
            var vfields = PayloadSpan.Fields(patched, vat, versionsField.End, doc, versionId);
            Assert.NotEmpty(vfields);
            int vend = vfields[^1].End;
            int causedBy = -2;
            foreach (var (fname, ffrom, fto) in vfields)
                if (fname == "m_causedByIncrementNumber" && fto - ffrom == 4)
                    causedBy = BitConverter.ToInt32(patched, ffrom);
            if (causedBy == -1)
            {
                foundMinusOne++;
                var totalVersion = vfields.First(f => f.Name == "m_totalElements");
                Assert.Equal(567684u, BitConverter.ToUInt32(patched, totalVersion.Start));
            }
            vat = vend;
        }
        Assert.Equal(1, foundMinusOne);
        // the earlier increments keep their historical counts
        Assert.NotEqual(567684u, BitConverter.ToUInt32(patched, local.Start + 4 + 0)); // sanity: bytes differ
    }

    [Fact]
    public void RefreshTotals_LeavesConsistentTableUntouched()
    {
        const string path = @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt";
        using var root = RootStorage.OpenRead(path);
        var doc = TfSchemaReader.Read(root)!;
        var data = PagedGzipStream.Read(root, "Global", "DocumentIncrementTable")!;
        var walker = new WideBlockWalker(doc, (d, f) => d.Name + "." + f.Name, ["DocumentIncrement"]);
        var walk = walker.Run(data, 0, data.Length, BinaryPrimitives.ReadUInt16LittleEndian(data));
        var last = walk.Records.Where(r => walker.Class(r.ClassId)?.Name == "DocumentIncrement").ToList()[^1];
        long current = (long)last.Values["m_totalElements"]!;
        var patched = DocumentIncrementFixup.RefreshTotals(data, doc, (uint)current);
        Assert.Equal(data, patched);
    }
}
