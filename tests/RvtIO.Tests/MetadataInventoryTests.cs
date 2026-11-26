using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OpenMcdf;
using RvtIO.Cli;
using Xunit;

namespace RvtIO.Tests;

public class MetadataInventoryTests
{
    private static byte[] BuildBasicFileInfoBytes(string? labelledText, params string[] lengthPrefixedStrings)
    {
        var bytes = new List<byte>(BitConverter.GetBytes(18));
        bytes.AddRange(new byte[8]);
        foreach (string value in lengthPrefixedStrings)
        {
            bytes.AddRange(BitConverter.GetBytes(value.Length));
            bytes.AddRange(Encoding.Unicode.GetBytes(value));
        }
        if (labelledText != null)
        {
            bytes.AddRange(new byte[2]);
            bytes.AddRange(Encoding.Unicode.GetBytes(labelledText));
            bytes.AddRange(new byte[2]);
        }
        return bytes.ToArray();
    }

    private static byte[] BuildProjectInfoZipStream(string xmlContent)
    {
        using var zipMs = new MemoryStream();
        using (var archive = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("project.xml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(xmlContent);
        }
        byte[] prefix = [0, 0, 0, 0];
        byte[] zipBytes = zipMs.ToArray();
        byte[] combined = new byte[prefix.Length + zipBytes.Length];
        Buffer.BlockCopy(prefix, 0, combined, 0, prefix.Length);
        Buffer.BlockCopy(zipBytes, 0, combined, prefix.Length, zipBytes.Length);
        return combined;
    }

    private static byte[] BuildTransmissionDataBytes(string userData, params string[] refPaths)
    {
        var sb = new StringBuilder();
        sb.Append($"<TransmissionData version=\"1\" isTransmitted=\"true\" userData=\"{System.Security.SecurityElement.Escape(userData)}\">");
        int id = 100;
        foreach (string path in refPaths)
        {
            id++;
            sb.Append("<ExternalFileReference>");
            sb.Append($"<ElementId>{id}</ElementId>");
            sb.Append("<ExternalFileReferenceType>RevitLink</ExternalFileReferenceType>");
            sb.Append($"<LastSavedPath>{System.Security.SecurityElement.Escape(path)}</LastSavedPath>");
            sb.Append($"<LastSavedAbsolutePath>{System.Security.SecurityElement.Escape(path)}</LastSavedAbsolutePath>");
            sb.Append("<LastSavedPathType>Relative</LastSavedPathType>");
            sb.Append("<LastSavedLoadState>Loaded</LastSavedLoadState>");
            sb.Append("</ExternalFileReference>");
        }
        sb.Append("</TransmissionData>");
        string xml = sb.ToString();
        byte[] xmlBytes = Encoding.Unicode.GetBytes(xml);
        byte[] result = new byte[4 + xmlBytes.Length];
        BitConverter.GetBytes(xml.Length).CopyTo(result, 0);
        xmlBytes.CopyTo(result, 4);
        return result;
    }

    private static void CreateSyntheticRevitFile(
        string path,
        bool isFamily,
        string? basicFileInfoText,
        string? projectXml = null,
        string? partAtomXml = null,
        byte[]? transmissionData = null,
        byte[]? partitionZeroPayload = null)
    {
        using var root = RootStorage.Create(path);
        if (basicFileInfoText != null)
        {
            using var s = root.CreateStream("BasicFileInfo");
            byte[] bfi = BuildBasicFileInfoBytes(basicFileInfoText);
            s.Write(bfi, 0, bfi.Length);
        }
        if (!isFamily && projectXml != null)
        {
            using var s = root.CreateStream("ProjectInformation");
            byte[] pi = BuildProjectInfoZipStream(projectXml);
            s.Write(pi, 0, pi.Length);
        }
        else if (!isFamily)
        {
            using var s = root.CreateStream("ProjectInformation");
        }
        if (isFamily)
        {
            using var s = root.CreateStream("PartAtom");
            byte[] pa = Encoding.UTF8.GetBytes(partAtomXml ?? "<?xml version=\"1.0\" encoding=\"UTF-8\"?><entry><title>Family</title></entry>");
            s.Write(pa, 0, pa.Length);
        }
        if (transmissionData != null)
        {
            using var s = root.CreateStream("TransmissionData");
            s.Write(transmissionData, 0, transmissionData.Length);
        }
        if (partitionZeroPayload != null)
        {
            var parts = root.CreateStorage("Partitions");
            using var p0 = parts.CreateStream("0");
            p0.Write(partitionZeroPayload, 0, partitionZeroPayload.Length);
        }
    }

    private sealed class BlockedRangeStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly long _blockedStart;
        private readonly long _blockedEnd;

        public BlockedRangeStream(byte[] buffer, long blockedStart, long blockedEnd)
        {
            _inner = new MemoryStream(buffer, writable: false);
            _blockedStart = blockedStart;
            _blockedEnd = blockedEnd;
        }

        public bool BlockedRangeTouched { get; private set; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            long start = _inner.Position;
            int read = _inner.Read(buffer, offset, count);
            long end = start + read;
            if (read > 0 && end > _blockedStart && start < _blockedEnd)
            {
                BlockedRangeTouched = true;
                throw new InvalidOperationException($"Forbidden read overlapped Partitions payload [{_blockedStart}, {_blockedEnd}) at [{start}, {end}).");
            }
            return read;
        }
    }

    private static List<string[]> ParseCsv(string csv)
    {
        var rows = new List<string[]>();
        var currentRow = new List<string>();
        var currentField = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < csv.Length; i++)
        {
            char c = csv[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        currentField.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    currentField.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                currentRow.Add(currentField.ToString());
                currentField.Clear();
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                currentRow.Add(currentField.ToString());
                currentField.Clear();
                rows.Add(currentRow.ToArray());
                currentRow.Clear();
            }
            else
            {
                currentField.Append(c);
            }
        }
        if (currentField.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(currentField.ToString());
            rows.Add(currentRow.ToArray());
        }
        return rows;
    }

    [Fact]
    public void ReadMetadataStream_DoesNotReadPartitionsPayload()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "rvtio-info-part-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "model.rvt");
            // Distinct repeating marker fills Partitions/0 (512 KB) so we can locate its exact container range.
            byte[] marker = new byte[512 * 1024];
            for (int i = 0; i < marker.Length; i++)
                marker[i] = (byte)(0xA0 + (i % 13));

            CreateSyntheticRevitFile(
                filePath,
                isFamily: false,
                basicFileInfoText:
                    "Worksharing: Local\r\nFormat: 2023\r\nBuild: 20220503_1030(x64)\r\n" +
                    "Locale when saved: RUS\r\nUnique Document Increments: 17\r\n",
                projectXml:
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?><entry><title>Demo</title><updated>2025-02-01T12:00:00Z</updated>" +
                    "<Наименование_проекта>ЖК \"Восход\", д. 1</Наименование_проекта></entry>",
                partitionZeroPayload: marker);

            byte[] fileBytes = File.ReadAllBytes(filePath);
            byte[] signature = marker[..32768];
            int idx = fileBytes.AsSpan().IndexOf(signature);
            Assert.True(idx > 0);

            using var guarded = new BlockedRangeStream(fileBytes, idx, idx + signature.Length);
            var summary = new RvtFileReader().ReadMetadataStream(guarded, new RvtMetadataOptions { SourcePath = filePath });

            Assert.False(guarded.BlockedRangeTouched);
            Assert.Equal("2023", summary.RevitVersion);
            Assert.Equal("20220503_1030(x64)", summary.Build);
            Assert.True(summary.IsWorkshared);
            Assert.Equal("Local", summary.Worksharing);
            Assert.Equal("RUS", summary.SavedLocale);
            Assert.Equal(17, summary.UniqueDocumentIncrements);
            Assert.Equal("2025-02-01T12:00:00Z", summary.LastSavedUtc);
            Assert.Equal("ЖК \"Восход\", д. 1", summary.ProjectInfo.Name);
            Assert.Equal(1, summary.PartitionCount);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ReadMetadata_DistinguishesTemplatesAndBackups_AndKeepsOmittedFieldsNull()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "rvtio-info-types-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string projectTemplate = Path.Combine(tempDir, "Шаблон.rte");
            string familyTemplate = Path.Combine(tempDir, "Семейство.rft");
            string backupProject = Path.Combine(tempDir, "Дом.0003.rvt");

            // Minimal BasicFileInfo omitting Worksharing, Build, Username, Central Model Path, IsSingleUserCloudModel.
            CreateSyntheticRevitFile(projectTemplate, isFamily: false, basicFileInfoText: "Format: 2024\r\nLocale when saved: ENU\r\n");
            CreateSyntheticRevitFile(familyTemplate, isFamily: true, basicFileInfoText: "Format: 2022\r\n");
            CreateSyntheticRevitFile(backupProject, isFamily: false, basicFileInfoText: "Worksharing: Not enabled\r\nFormat: 2025\r\n");

            var reader = new RvtFileReader();
            var rte = reader.ReadMetadata(projectTemplate);
            var rft = reader.ReadMetadata(familyTemplate);
            var bak = reader.ReadMetadata(backupProject);

            Assert.Equal(RvtFileKind.Project, rte.FileKind);
            Assert.Equal(RvtDocumentKind.ProjectTemplate, rte.DocumentKind);
            Assert.Equal("rte", rte.FileType);
            Assert.True(rte.IsTemplate);
            Assert.False(rte.IsBackup);
            Assert.Null(rte.BackupNumber);
            Assert.Null(rte.Worksharing);
            Assert.Null(rte.IsWorkshared);
            Assert.Null(rte.IsCloudModel);
            Assert.Null(rte.Build);
            Assert.Null(rte.Username);
            Assert.Null(rte.CentralModelPath);

            Assert.Equal(RvtFileKind.Family, rft.FileKind);
            Assert.Equal(RvtDocumentKind.FamilyTemplate, rft.DocumentKind);
            Assert.Equal("rft", rft.FileType);
            Assert.True(rft.IsTemplate);
            Assert.True(rft.IsStandaloneFamily);

            Assert.Equal(RvtFileKind.Project, bak.FileKind);
            Assert.Equal(RvtDocumentKind.Project, bak.DocumentKind);
            Assert.True(bak.IsBackup);
            Assert.Equal(3, bak.BackupNumber);
            Assert.False(bak.IsWorkshared);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ReadMetadata_PreservesCyrillicStrings_AndRedactsPersonalDataAcrossEveryField()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "rvtio-info-redact-" + Guid.NewGuid().ToString("N"));
        string userSubdir = Path.Combine(tempDir, "Users", "alice_ivanova", "Проект Юг, секция 1");
        Directory.CreateDirectory(userSubdir);
        try
        {
            string rvtPath = Path.Combine(userSubdir, "Дом_alice_ivanova.rvt");
            CreateSyntheticRevitFile(
                rvtPath,
                isFamily: false,
                basicFileInfoText:
                    "Worksharing: Central\r\n" +
                    "Username: alice_ivanova\r\n" +
                    "Central Model Path: \\\\corp-fileserver\\BIM\\alice_ivanova\\Анна Смирнова\\Центр.rvt\r\n" +
                    "Format: 2024\r\n" +
                    "Build: 20240101_1500(x64)\r\n" +
                    "Last Save Path: C:\\Users\\alice_ivanova\\OneDrive - Acme Corp\\Мария Сидорова\\Документы\\Модель \"А\", ч.2.rvt\r\n" +
                    "Locale when saved: RUS\r\n" +
                    "Author: Мария Сидорова\r\n",
                projectXml:
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?><entry><title>Проект \"Альфа\", 1</title><updated>2026-03-10T08:30:00Z</updated>" +
                    "<Наименование_проекта>ЖК \"Север\"</Наименование_проекта>" +
                    "<Разработал>Анна Смирнова</Разработал>" +
                    "<Заказчик>bob@example.com</Заказчик></entry>",
                transmissionData: BuildTransmissionDataBytes("alice_ivanova", @"C:\Users\alice_ivanova\Links\Ось.rvt"));

            var reader = new RvtFileReader();
            var raw = reader.ReadMetadata(rvtPath);

            Assert.Equal("alice_ivanova", raw.Username);
            Assert.Contains("Документы", raw.LastSavePath);
            Assert.Contains("Модель \"А\", ч.2.rvt", raw.LastSavePath);
            Assert.Contains("Центр.rvt", raw.CentralModelPath);
            Assert.Equal("Мария Сидорова", raw.Author);
            Assert.Equal("Проект \"Альфа\", 1", raw.ProjectInfo.Title);

            var redacted = reader.ReadMetadata(rvtPath, new RvtMetadataOptions { RedactPersonalData = true });
            Assert.Equal("<redacted>", redacted.Username);
            Assert.Equal("<redacted>", redacted.Author);
            Assert.Equal("<redacted>", redacted.ProjectInfo.Author);
            Assert.Equal("<redacted>", redacted.ProjectInfo.Client);
            Assert.Equal("<redacted>", redacted.TransmissionData!.UserData);
            Assert.DoesNotContain("alice_ivanova", redacted.FilePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alice_ivanova", redacted.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alice_ivanova", redacted.LastSavePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Acme Corp", redacted.LastSavePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Мария Сидорова", redacted.LastSavePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("corp-fileserver", redacted.CentralModelPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alice_ivanova", redacted.CentralModelPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Анна Смирнова", redacted.CentralModelPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alice_ivanova", redacted.TransmissionData.ExternalReferences[0].Path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alice_ivanova", redacted.BasicFileInfoFields["Username"], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Мария Сидорова", redacted.BasicFileInfoFields["Author"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Документы", redacted.LastSavePath);
            Assert.Equal("$&", raw.Redact("$&").ProjectInfo.Client);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ReadMetadata_PreservesSupplementaryCharacters()
    {
        var dir = Directory.CreateTempSubdirectory("rvtio-info-unicode-");
        try
        {
            string input = Path.Combine(dir.FullName, "model.rvt");
            const string username = "Before\U0001F600After";
            CreateSyntheticRevitFile(input, isFamily: false, basicFileInfoText: $"Username: {username}\r\nFormat: 2023\r\n");

            var summary = new RvtFileReader().ReadMetadata(input);

            Assert.Equal(username, summary.Username);
            Assert.Equal("2023", summary.RevitVersion);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(int.MaxValue)]
    public void CliInfo_DistinguishesDamagedTransmissionDataFromNoLinks(int declaredLength)
    {
        var dir = Directory.CreateTempSubdirectory("rvtio-info-transmission-");
        try
        {
            string broken = Path.Combine(dir.FullName, "broken.rvt");
            byte[] truncated = new byte[8];
            BitConverter.GetBytes(declaredLength).CopyTo(truncated, 0);
            CreateSyntheticRevitFile(broken, isFamily: false, basicFileInfoText: "Format: 2023\r\n", transmissionData: truncated);
            string good = Path.Combine(dir.FullName, "good.rvt");
            CreateSyntheticRevitFile(good, isFamily: false, basicFileInfoText: "Format: 2023\r\n",
                transmissionData: BuildTransmissionDataBytes(""));
            using (var stream = File.OpenRead(broken))
            using (var container = RootStorage.Open(stream, StorageModeFlags.LeaveOpen))
                Assert.Null(Parsing.TransmissionDataReader.Read(container));
            var output = new StringWriter();

            int exitCode = InfoCommand.Run([dir.FullName, "--json"], output, TextWriter.Null);

            Assert.Equal(RvtExitCodes.InputError, exitCode);
            using var report = JsonDocument.Parse(output.ToString());
            var rows = report.RootElement.GetProperty("files");
            Assert.Equal("broken.rvt", rows[0].GetProperty("fileName").GetString());
            Assert.Equal("error", rows[0].GetProperty("status").GetString());
            Assert.Equal("truncated-or-damaged", rows[0].GetProperty("errorKind").GetString());
            Assert.Contains("TransmissionData", rows[0].GetProperty("error").GetString());
            Assert.Equal("ok", rows[1].GetProperty("status").GetString());
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void CliInfo_FolderOutputCannotReplaceAnInputModel()
    {
        var dir = Directory.CreateTempSubdirectory("rvtio-info-collision-");
        try
        {
            string input = Path.Combine(dir.FullName, "model.rvt");
            CreateSyntheticRevitFile(input, isFamily: false, basicFileInfoText: "Format: 2023\r\n");
            byte[] original = File.ReadAllBytes(input);
            var error = new StringWriter();

            int exitCode = InfoCommand.Run([dir.FullName, "--json", "--output", input], TextWriter.Null, error);

            Assert.Equal(RvtExitCodes.Usage, exitCode);
            Assert.Contains("output path is an input file", error.ToString());
            Assert.Equal(original, File.ReadAllBytes(input));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CliInfo_RedactsFileNamesInReadErrors(bool fileExists)
    {
        var dir = Directory.CreateTempSubdirectory("rvtio-info-error-");
        try
        {
            const string email = "probe.author@example.test";
            string input = Path.Combine(dir.FullName, email + ".rvt");
            if (fileExists) File.WriteAllText(input, "Invalid CFB file");
            var error = new StringWriter();

            int exitCode = InfoCommand.Run([input, "--redact"], TextWriter.Null, error);

            Assert.Equal(RvtExitCodes.InputError, exitCode);
            Assert.StartsWith("Cannot read <redacted>", error.ToString());
            Assert.DoesNotContain(email, error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void CliInfo_RedactsFileNamesInWriteErrors()
    {
        var dir = Directory.CreateTempSubdirectory("rvtio-info-output-error-");
        try
        {
            string input = Path.Combine(dir.FullName, "model.rvt");
            CreateSyntheticRevitFile(input, isFamily: false, basicFileInfoText: "Format: 2023\r\n");
            const string email = "probe.author@example.test";
            string output = Path.Combine(dir.FullName, email + ".json");
            Directory.CreateDirectory(output);
            var error = new StringWriter();

            int exitCode = InfoCommand.Run([input, "--output", output, "--redact"], TextWriter.Null, error);

            Assert.Equal(RvtExitCodes.Usage, exitCode);
            Assert.StartsWith("Cannot write ", error.ToString());
            Assert.DoesNotContain(email, error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { dir.Delete(recursive: true); }
    }

    [SkippableFact]
    public void CliInfo_DoesNotTraverseDirectoryLinks()
    {
        var root = Directory.CreateTempSubdirectory("rvtio-info-linked-");
        string inventory = Path.Combine(root.FullName, "inventory");
        string outside = Path.Combine(root.FullName, "outside");
        string link = Path.Combine(inventory, "link");
        try
        {
            Directory.CreateDirectory(inventory);
            Directory.CreateDirectory(outside);
            CreateSyntheticRevitFile(Path.Combine(outside, "outside.rvt"), isFamily: false, basicFileInfoText: "Format: 2023\r\n");
            if (OperatingSystem.IsWindows()) ExportReportTests.Junction(link, outside);
            else ExportReportTests.DirectorySymlink(link, outside);
            var output = new StringWriter();

            int exitCode = InfoCommand.Run([inventory, "--json"], output, TextWriter.Null);

            Assert.Equal(RvtExitCodes.Success, exitCode);
            using var report = JsonDocument.Parse(output.ToString());
            Assert.Empty(report.RootElement.GetProperty("files").EnumerateArray());
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void CliInfo_FolderInventory_OutputsCsvAndJsonlWithErrorsRedactionAndNonZeroExit()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "rvtio-info-cli-" + Guid.NewGuid().ToString("N"));
        string nestedDir = Path.Combine(tempDir, "Users", "petrov", "Архив, 2026");
        Directory.CreateDirectory(nestedDir);
        try
        {
            string topRvt = Path.Combine(tempDir, "Главный.rvt");
            string nestedRfa = Path.Combine(nestedDir, "Дверь.0002.rfa");
            string brokenRvt = Path.Combine(nestedDir, "Битый.rvt");

            CreateSyntheticRevitFile(
                topRvt,
                isFamily: false,
                basicFileInfoText:
                    "Worksharing: Local\r\nUsername: petrov\r\nFormat: 2023\r\n" +
                    "Last Save Path: C:\\Users\\petrov\\OneDrive - SecretOrg\\Проект \"1, 2\".rvt\r\n");
            CreateSyntheticRevitFile(
                nestedRfa,
                isFamily: true,
                basicFileInfoText: "Format: 2022\r\nWorksharing: Not enabled\r\n");
            File.WriteAllBytes(brokenRvt, Encoding.UTF8.GetBytes("not a revit compound file at all"));

            // 1. Recursive CSV with redaction
            var csvOut = new StringWriter();
            var csvErr = new StringWriter();
            int exitCsv = InfoCommand.Run([tempDir, "--csv", "--redact"], csvOut, csvErr);

            Assert.Equal(RvtExitCodes.InputError, exitCsv);
            string csvText = csvOut.ToString();
            Assert.DoesNotContain("petrov", csvText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SecretOrg", csvText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("petrov", csvErr.ToString(), StringComparison.OrdinalIgnoreCase);

            List<string[]> csvRows = ParseCsv(csvText);
            Assert.Equal(4, csvRows.Count); // header + 3 files
            int colCount = csvRows[0].Length;
            Assert.All(csvRows, row => Assert.Equal(colCount, row.Length));

            int statusIdx = Array.IndexOf(csvRows[0], "status");
            int errorIdx = Array.IndexOf(csvRows[0], "error");
            int pathIdx = Array.IndexOf(csvRows[0], "path");
            int lastSaveIdx = Array.IndexOf(csvRows[0], "lastSavePath");
            int backupNumIdx = Array.IndexOf(csvRows[0], "backupNumber");

            Assert.Equal(2, csvRows.Skip(1).Count(r => r[statusIdx] == "ok"));
            var errRow = csvRows.Skip(1).Single(r => r[statusIdx] == "error");
            Assert.False(string.IsNullOrWhiteSpace(errRow[errorIdx]));

            var topRow = csvRows.Skip(1).Single(r => r[pathIdx].EndsWith("Главный.rvt", StringComparison.Ordinal));
            Assert.Contains("Проект \"1, 2\".rvt", topRow[lastSaveIdx]);

            var rfaRow = csvRows.Skip(1).Single(r => r[pathIdx].EndsWith("Дверь.0002.rfa", StringComparison.Ordinal));
            Assert.Equal("2", rfaRow[backupNumIdx]);

            // 2. Recursive JSONL check: omitted fields remain JSON null, every line parses as JSON
            var jsonlOut = new StringWriter();
            var jsonlErr = new StringWriter();
            int exitJsonl = InfoCommand.Run([tempDir, "--format", "jsonl", "--redact"], jsonlOut, jsonlErr);
            Assert.Equal(RvtExitCodes.InputError, exitJsonl);
            string jsonlText = jsonlOut.ToString();
            Assert.DoesNotContain("petrov", jsonlText, StringComparison.OrdinalIgnoreCase);

            string[] lines = jsonlText.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, lines.Length);
            foreach (string line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
                string fileName = doc.RootElement.GetProperty("fileName").GetString() ?? "";
                if (fileName == "Дверь.0002.rfa")
                {
                    Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
                    Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("build").ValueKind);
                    Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("username").ValueKind);
                }
            }

            // 3. Non-recursive scan sees only top-level healthy file and exits 0
            var topOut = new StringWriter();
            var topErr = new StringWriter();
            int exitTop = InfoCommand.Run([tempDir, "--jsonl", "--no-recursive"], topOut, topErr);
            Assert.Equal(RvtExitCodes.Success, exitTop);
            string[] topLines = topOut.ToString().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(topLines);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [SkippableFact]
    public void SampleRvtFilesFromTestRoots_LightweightMetadataMatchesFullReadAndSkipsPartitions()
    {
        string? envRoots = Environment.GetEnvironmentVariable("RVTIO_TEST_FILES");
        if (string.IsNullOrWhiteSpace(envRoots)) return;

        var allCandidates = envRoots!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".rvt" or ".rfa"))
            .Select(p => new FileInfo(p))
            .ToList();

        var samples = new List<string>();
        var smallestRvt = allCandidates.Where(fi => fi.Extension.Equals(".rvt", StringComparison.OrdinalIgnoreCase)).OrderBy(fi => fi.Length).FirstOrDefault();
        var smallestRfa = allCandidates.Where(fi => fi.Extension.Equals(".rfa", StringComparison.OrdinalIgnoreCase)).OrderBy(fi => fi.Length).FirstOrDefault();
        if (smallestRvt != null) samples.Add(smallestRvt.FullName);
        if (smallestRfa != null) samples.Add(smallestRfa.FullName);

        if (samples.Count == 0) return;

        var reader = new RvtFileReader();
        foreach (string samplePath in samples)
        {
            var lightweight = reader.ReadMetadata(samplePath);
            var full = reader.Read(samplePath);

            Assert.Equal(full.Metadata.StreamVersion, lightweight.Metadata.StreamVersion);
            Assert.Equal(full.Metadata.RevitVersion, lightweight.Metadata.RevitVersion);
            Assert.Equal(full.Metadata.Build, lightweight.Metadata.Build);
            Assert.Equal(full.Metadata.LastSavePath, lightweight.Metadata.LastSavePath);
            Assert.Equal(full.Metadata.SavedLocale, lightweight.Metadata.SavedLocale);
            Assert.Equal(full.Metadata.PreviewImageMimeType, lightweight.Metadata.PreviewImageMimeType);

            Assert.Equal(full.ProjectInfo.Name, lightweight.ProjectInfo.Name);
            Assert.Equal(full.ProjectInfo.Number, lightweight.ProjectInfo.Number);
            Assert.Equal(full.ProjectInfo.Client, lightweight.ProjectInfo.Client);
            Assert.Equal(full.ProjectInfo.Address, lightweight.ProjectInfo.Address);
            Assert.Equal(full.ProjectInfo.Status, lightweight.ProjectInfo.Status);
            Assert.Equal(full.ProjectInfo.IssueDate, lightweight.ProjectInfo.IssueDate);
            Assert.Equal(full.ProjectInfo.Title, lightweight.ProjectInfo.Title);
            Assert.Equal(full.ProjectInfo.RevitVersion, lightweight.ProjectInfo.RevitVersion);
            Assert.Equal(full.ProjectInfo.Author, lightweight.ProjectInfo.Author);
            Assert.Equal(full.ProjectInfo.BuildingName, lightweight.ProjectInfo.BuildingName);
            Assert.Equal(full.ProjectInfo.OrganizationName, lightweight.ProjectInfo.OrganizationName);

            Assert.Equal(full.SerializedIsWorkshared, lightweight.IsWorkshared);
            Assert.Equal(full.IsStandaloneFamily, lightweight.IsStandaloneFamily);
            Assert.Equal(full.TransmissionData?.IsTransmitted, lightweight.TransmissionData?.IsTransmitted);
            Assert.Equal(full.TransmissionData?.ExternalReferences.Count, lightweight.TransmissionData?.ExternalReferences.Count);
        }

        // Also corrupt Partitions/0 payload on an in-memory copy of the first real sample with partitions:
        // full ReadStream rejects it, while ReadMetadataStream succeeds identically.
        string? withPartitions = samples.FirstOrDefault(p => reader.ReadMetadata(p).PartitionCount > 0);
        if (withPartitions != null)
        {
            byte[] copy = File.ReadAllBytes(withPartitions);
            using var ms = new MemoryStream(copy);
            using (var root = RootStorage.Open(ms, StorageModeFlags.LeaveOpen | StorageModeFlags.Transacted))
            {
                var part = root.OpenStorage("Partitions");
                string firstPartitionName = part.EnumerateEntries().First(e => e.Type == EntryType.Stream).Name;
                using var p0 = part.OpenStream(firstPartitionName);
                byte[] garbage = Enumerable.Repeat((byte)0xFF, Math.Min((int)p0.Length, 4096)).ToArray();
                p0.Position = 0;
                p0.Write(garbage, 0, garbage.Length);
                root.Commit();
            }

            ms.Position = 0;
            var metaFromCorruptPartitions = reader.ReadMetadataStream(ms, new RvtMetadataOptions { SourcePath = withPartitions });
            Assert.Equal(reader.ReadMetadata(withPartitions).RevitVersion, metaFromCorruptPartitions.RevitVersion);
        }
    }
}
