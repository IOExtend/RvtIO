using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpDropSchemaPair
{
    private readonly ITestOutputHelper _out;
    public TmpDropSchemaPair(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Patch()
    {
        var guid = new Guid("1b022a2b-5722-4787-928a-4f29ca9d8811");
        byte[] guidBytes = guid.ToByteArray();
        const string src = @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\drop20_FamilyInstance.rvt";
        const string dst = @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\drop20_noFI_noschema.rvt";
        File.Delete(dst);
        using var root = RootStorage.OpenRead(src);
        byte[] storedRaw;
        using (var ls = root.OpenStorage("Global").OpenStream("Latest"))
        {
            storedRaw = new byte[ls.Length];
            ls.ReadExactly(storedRaw);
        }
        var prefix = PagedStreamCodec.ReadPrefix(storedRaw);
        var latest = PagedGzipStream.Decompress(storedRaw)!;

        // find the pair item starting with our guid inside the 176-entry map
        // find ALL occurrences of the guid
        var hits = new List<int>();
        for (int i = 0; i + 16 <= latest.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < 16; j++) if (latest[i + j] != guidBytes[j]) { match = false; break; }
            if (match) { hits.Add(i); i += 15; }
        }
        _out.WriteLine($"guid hits={hits.Count} at [{string.Join(",", hits)}]");
        foreach (int h in hits)
            _out.WriteLine($"  [{h - 8}]: {Convert.ToHexString(latest.AsSpan(Math.Max(0, h - 8), 48))}");
        int guidAt = hits[0];
        // item = guid(16) + handle(4)=FFFFFFFF + tag(2)
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(latest.AsSpan(guidAt + 16, 4)));
        // find the array count: position C with count=176, C+4 first item, (guidAt-(C+4))%22==0, handles validate
        int countAt = -1;
        for (int c = guidAt - 4; c >= Math.Max(0, guidAt - 5000); c--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(latest.AsSpan(c, 4)) != 176) continue;
            int first = c + 4;
            int k = guidAt - first;
            if (k < 0 || k % 22 != 0 || k / 22 >= 176) continue;
            bool handles = true;
            for (int item = 0; item < 176; item++)
            {
                int h = first + item * 22 + 16;
                if (BinaryPrimitives.ReadUInt32LittleEndian(latest.AsSpan(h, 4)) != 0xFFFFFFFF) { handles = false; break; }
            }
            if (handles) { countAt = c; break; }
        }
        _out.WriteLine($"count at {countAt}, item index={(guidAt - countAt - 4) / 22}");
        Assert.True(countAt >= 0);

        int itemStart = guidAt, itemEnd = guidAt + 22;
        var next = new byte[latest.Length - 22];
        Buffer.BlockCopy(latest, 0, next, 0, itemStart);
        Buffer.BlockCopy(latest, itemEnd, next, itemStart, latest.Length - itemEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(next.AsSpan(countAt, 4), 175);
        var encoded = PagedStreamCodec.Encode(prefix, next);
        var result = ContainerRewriter.Rewrite(src, dst, new Dictionary<string, byte[]> { ["Global/Latest"] = encoded });
        _out.WriteLine($"written {dst}: replaced={result.Replaced}");
    }
}
