using System.Text.RegularExpressions;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class RvtPiiRedactor
{
    private const string DefaultToken = "<redacted>";

    [GeneratedRegex(@"(?i)([A-Za-z]:)?([\\/](?:Users|Documents and Settings)|/(?:Users|home))[\\/][^\\/\r\n'""]+")]
    private static partial Regex UserProfileSegment();

    [GeneratedRegex(@"(?i)(OneDrive\s*-\s*)[^\\/\r\n'""]+")]
    private static partial Regex OneDriveTenant();

    [GeneratedRegex(@"(?<!:)(\\\\|//)[^\\/\r\n\s'""]+")]
    private static partial Regex UncHost();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailAddress();

    internal static RvtFileMetadataSummary RedactSummary(RvtFileMetadataSummary summary, string token = DefaultToken)
    {
        var sensitiveTokens = CollectSensitiveTokens(
            summary.Username,
            summary.TransmissionData?.UserData,
            summary.FilePath,
            summary.LastSavePath,
            summary.CentralModelPath);

        string? filePath = RedactText(summary.FilePath, token, sensitiveTokens);
        string? fileName = RedactText(summary.FileName, token, sensitiveTokens);
        string? lastSavePath = RedactText(summary.LastSavePath, token, sensitiveTokens);
        string? centralModelPath = RedactText(summary.CentralModelPath, token, sensitiveTokens);
        string? username = RedactUsernameField(summary.Username, token);
        string? author = RedactAuthorField(summary.Author, token, sensitiveTokens);

        var metadata = new FileMetadata
        {
            StreamVersion = summary.Metadata.StreamVersion,
            RevitVersion = summary.Metadata.RevitVersion,
            Build = RedactText(summary.Metadata.Build, token, sensitiveTokens),
            LastSavePath = lastSavePath,
            SavedLocale = summary.Metadata.SavedLocale,
            PreviewImage = summary.Metadata.PreviewImage,
            PreviewImageMimeType = summary.Metadata.PreviewImageMimeType,
        };

        var projectInfo = new ProjectInfo
        {
            Name = RedactText(summary.ProjectInfo.Name, token, sensitiveTokens),
            Number = RedactText(summary.ProjectInfo.Number, token, sensitiveTokens),
            Client = RedactText(summary.ProjectInfo.Client, token, sensitiveTokens),
            Address = RedactText(summary.ProjectInfo.Address, token, sensitiveTokens),
            Status = RedactText(summary.ProjectInfo.Status, token, sensitiveTokens),
            IssueDate = RedactText(summary.ProjectInfo.IssueDate, token, sensitiveTokens),
            Title = RedactText(summary.ProjectInfo.Title, token, sensitiveTokens),
            RevitVersion = summary.ProjectInfo.RevitVersion,
            Author = RedactAuthorField(summary.ProjectInfo.Author, token, sensitiveTokens),
            BuildingName = RedactText(summary.ProjectInfo.BuildingName, token, sensitiveTokens),
            OrganizationName = RedactText(summary.ProjectInfo.OrganizationName, token, sensitiveTokens),
        };

        TransmissionInfo? transmissionData = null;
        if (summary.TransmissionData is { } td)
        {
            transmissionData = new TransmissionInfo
            {
                IsTransmitted = td.IsTransmitted,
                Version = td.Version,
                UserData = string.IsNullOrEmpty(td.UserData) ? td.UserData : token,
                ExternalReferences = td.ExternalReferences.Select(ext => new ExternalFileRef
                {
                    ElementId = ext.ElementId,
                    ReferenceType = ext.ReferenceType,
                    Path = RedactText(ext.Path, token, sensitiveTokens),
                    AbsolutePath = RedactText(ext.AbsolutePath, token, sensitiveTokens),
                    PathType = ext.PathType,
                    LoadState = ext.LoadState,
                    ServerLocation = RedactText(ext.ServerLocation, token, sensitiveTokens),
                }).ToList(),
            };
        }

        var redactedFields = new Dictionary<string, string>(summary.BasicFileInfoFields.Count, StringComparer.Ordinal);
        foreach (var (key, value) in summary.BasicFileInfoFields)
        {
            if (string.Equals(key, "Username", StringComparison.OrdinalIgnoreCase))
            {
                redactedFields[key] = string.IsNullOrEmpty(value) ? value : token;
            }
            else if (string.Equals(key, "Author", StringComparison.OrdinalIgnoreCase))
            {
                redactedFields[key] = RedactAuthorField(value, token, sensitiveTokens) ?? "";
            }
            else
            {
                redactedFields[key] = RedactText(value, token, sensitiveTokens) ?? value;
            }
        }

        return new RvtFileMetadataSummary
        {
            FilePath = filePath,
            FileName = fileName,
            Extension = summary.Extension,
            FileSizeBytes = summary.FileSizeBytes,
            FileKind = summary.FileKind,
            DocumentKind = summary.DocumentKind,
            FileType = summary.FileType,
            IsTemplate = summary.IsTemplate,
            IsBackup = summary.IsBackup,
            BackupNumber = summary.BackupNumber,
            IsStandaloneFamily = summary.IsStandaloneFamily,
            RevitVersion = summary.RevitVersion,
            Build = RedactText(summary.Build, token, sensitiveTokens),
            FullBuild = RedactText(summary.FullBuild, token, sensitiveTokens),
            ClientAppName = summary.ClientAppName,
            StreamVersion = summary.StreamVersion,
            Worksharing = summary.Worksharing,
            IsWorkshared = summary.IsWorkshared,
            IsCloudModel = summary.IsCloudModel,
            SavedLocale = summary.SavedLocale,
            LastSavePath = lastSavePath,
            CentralModelPath = centralModelPath,
            Username = username,
            Author = author,
            LastSavedUtc = summary.LastSavedUtc,
            UniqueDocumentGuid = summary.UniqueDocumentGuid,
            UniqueDocumentIncrements = summary.UniqueDocumentIncrements,
            CentralModelIdentity = summary.CentralModelIdentity,
            ModelIdentity = summary.ModelIdentity,
            CentralModelVersion = summary.CentralModelVersion,
            CentralModelEpisodeGuid = summary.CentralModelEpisodeGuid,
            AllLocalChangesSavedToCentral = summary.AllLocalChangesSavedToCentral,
            OpenWorksetDefault = summary.OpenWorksetDefault,
            IsProjectSparkFile = summary.IsProjectSparkFile,
            Metadata = metadata,
            ProjectInfo = projectInfo,
            TransmissionData = transmissionData,
            HasBasicFileInfo = summary.HasBasicFileInfo,
            HasProjectInformation = summary.HasProjectInformation,
            HasPartAtom = summary.HasPartAtom,
            HasTransmissionData = summary.HasTransmissionData,
            HasPreviewImage = summary.HasPreviewImage,
            PreviewImageMimeType = summary.PreviewImageMimeType,
            ExternalReferenceCount = summary.ExternalReferenceCount,
            IsTransmitted = summary.IsTransmitted,
            StreamCount = summary.StreamCount,
            PartitionCount = summary.PartitionCount,
            BasicFileInfoFields = redactedFields,
        };
    }

    internal static IReadOnlyList<string> CollectSensitiveTokens(params string?[] candidates)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string trimmed = candidate.Trim();
            if (!LooksLikePath(trimmed))
            {
                AddToken(tokens, trimmed);
                int slash = trimmed.IndexOfAny(['\\', '/']);
                if (slash >= 0 && slash + 1 < trimmed.Length)
                {
                    AddToken(tokens, trimmed[..slash]);
                    AddToken(tokens, trimmed[(slash + 1)..]);
                }
            }
            foreach (Match match in UserProfileSegment().Matches(trimmed))
            {
                string full = match.Value;
                int lastSep = full.LastIndexOfAny(['\\', '/']);
                if (lastSep >= 0 && lastSep + 1 < full.Length)
                    AddToken(tokens, full[(lastSep + 1)..]);
            }
        }
        return tokens.OrderByDescending(t => t.Length).ToArray();
    }

    internal static string? RedactText(string? value, string token = DefaultToken, IReadOnlyList<string>? extraTokens = null)
    {
        if (string.IsNullOrEmpty(value)) return value;
        string result = UserProfileSegment().Replace(value, match =>
        {
            string drive = match.Groups[1].Value;
            string root = match.Groups[2].Value;
            char sep = root.Length > 0 && (root[0] == '\\' || root.Contains('\\')) ? '\\' : '/';
            return $"{drive}{root}{sep}{token}";
        });
        result = OneDriveTenant().Replace(result, $"$1{token}");
        result = UncHost().Replace(result, $"$1{token}");
        result = EmailAddress().Replace(result, token);
        if (extraTokens is { Count: > 0 })
        {
            foreach (string rawToken in extraTokens)
            {
                if (string.Equals(rawToken, token, StringComparison.OrdinalIgnoreCase)) continue;
                string pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(rawToken)}(?![\p{{L}}\p{{N}}])";
                result = Regex.Replace(result, pattern, token, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }
        return result;
    }

    private static string? RedactUsernameField(string? value, string token) =>
        string.IsNullOrWhiteSpace(value) ? value : token;

    private static string? RedactAuthorField(string? value, string token, IReadOnlyList<string> sensitiveTokens)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        string trimmed = value.Trim();
        if (trimmed.Equals("Autodesk Revit", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Autodesk", StringComparison.OrdinalIgnoreCase))
            return value;
        _ = sensitiveTokens;
        return token;
    }

    private static void AddToken(HashSet<string> tokens, string candidate)
    {
        string trimmed = candidate.Trim();
        if (trimmed.Length < 2) return;
        if (trimmed.Equals("Autodesk", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Autodesk Revit", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Public", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Default", StringComparison.OrdinalIgnoreCase))
            return;
        tokens.Add(trimmed);
    }

    private static bool LooksLikePath(string value) =>
        value.Contains(":\\", StringComparison.Ordinal)
        || value.Contains(":/", StringComparison.Ordinal)
        || value.StartsWith("\\\\", StringComparison.Ordinal)
        || value.StartsWith("/", StringComparison.Ordinal);
}
