using System.Globalization;
using System.Text.Json;
using RvtIO.BuiltIns;

namespace RvtIO.Cli;

internal static class SetParameterCommand
{
    internal static int Run(string[] args, TextWriter output, TextWriter error, bool familyFile = false)
    {
        long elementId = 0;
        long parameterId = 0;
        if (args.Length != 5 || args.Take(3).Any(string.IsNullOrWhiteSpace) || !TryParseParameter(args[3], out parameterId)
            || (!familyFile && (!long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out elementId) || elementId <= 0)))
        {
            error.WriteLine(familyFile
                ? "Usage: rvtio set-family-parameter <source.rfa> <output.rfa> <type-name> <parameter-id|built-in-name> <value>"
                : "Usage: rvtio set-parameter <source.rvt> <output.rvt> <element-id> <parameter-id|built-in-name> <value>");
            return RvtExitCodes.Usage;
        }

        return Write(() => familyFile ? RvtWriter.SetFamilyParameter(args[0], args[1], args[2], parameterId, args[4])
            : RvtWriter.SetParameter(args[0], args[1], elementId, parameterId, args[4]), output, error);
    }

    internal static int RunBatch(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 3 || args.Any(string.IsNullOrWhiteSpace))
        {
            error.WriteLine("Usage: rvtio edit-parameters <source.rvt|source.rfa> <output.rvt|output.rfa> <edits.json>");
            return RvtExitCodes.Usage;
        }

        return Write(() =>
        {
            using var file = File.OpenRead(args[2]);
            using var json = JsonDocument.Parse(file);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0)
                throw new JsonException("Expected a non-empty array of edits.");
            bool family = Path.GetExtension(args[0]).Equals(".rfa", StringComparison.OrdinalIgnoreCase);
            var projectEdits = new List<RvtParameterEdit>();
            var familyEdits = new List<RvtFamilyParameterEdit>();
            foreach (var entry in json.RootElement.EnumerateArray())
            {
                int index = (family ? familyEdits.Count : projectEdits.Count) + 1;
                if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().Count() != 3
                    || !entry.TryGetProperty("parameterId", out var parameter) || parameter.ValueKind != JsonValueKind.Number
                    || !parameter.TryGetInt64(out long id) || id == 0
                    || !entry.TryGetProperty("value", out var value) || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw new JsonException($"Edit {index} must have a target, an integer parameterId, and a string or null value.");
                if (family)
                {
                    if (!entry.TryGetProperty("typeName", out var type) || type.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(type.GetString()))
                        throw new JsonException($"Edit {index} requires a family typeName.");
                    familyEdits.Add(new(type.GetString()!, id, value.GetString()));
                }
                else
                {
                    if (!entry.TryGetProperty("elementId", out var element) || element.ValueKind != JsonValueKind.Number
                        || !element.TryGetInt64(out long elementId) || elementId <= 0)
                        throw new JsonException($"Edit {index} requires a positive integer elementId.");
                    projectEdits.Add(new(elementId, id, value.GetString()));
                }
            }
            return family ? RvtWriter.SetFamilyParameters(args[0], args[1], familyEdits)
                : RvtWriter.SetParameters(args[0], args[1], projectEdits);
        }, output, error);
    }

    private static int Write(Func<RvtWriteResult> save, TextWriter output, TextWriter error)
    {
        try
        {
            var result = save();
            output.WriteLine(result.ReplacedStreamCount == 0
                ? $"Saved: {Path.GetFileName(result.OutputPath)} (value already matched)"
                : $"Saved: {Path.GetFileName(result.OutputPath)}");
            return RvtExitCodes.Success;
        }
        catch (JsonException ex)
        {
            error.WriteLine($"error: {ex.Message}");
            return RvtExitCodes.Usage;
        }
        catch (RvtWriteRefusedException ex)
        {
            error.WriteLine($"error [{ex.Kind}]: {ex.Message}");
            return SaveAsCommand.Refused;
        }
        catch (Exception ex) when (ex is RvtInputException or RvtLimitException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            error.WriteLine($"error: {ex.Message}");
            return RvtExitCodes.InputError;
        }
    }

    private static bool TryParseParameter(string text, out long id)
    {
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id))
            return id != 0;
        if (!Enum.IsDefined(typeof(BuiltInParameter), text) || !Enum.TryParse<BuiltInParameter>(text, out var parameter))
            return false;
        id = (int)parameter;
        return true;
    }
}
