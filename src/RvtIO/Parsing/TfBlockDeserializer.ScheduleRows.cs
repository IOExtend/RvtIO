namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    internal sealed class ScheduleRowBindings
    {
        public readonly Dictionary<int, (int Parameter, bool Hidden, int Source)> Fields = new();
        public readonly Dictionary<int, (int Field, int GroupFlags, int Order)> Sorts = new();
        public readonly Dictionary<int, (int Field, int Type, int Value, int Storage)> Filters = new();
        public List<int> FieldIds = new(), FieldObjects = new(), SortObjects = new(), FilterObjects = new();
        public bool Itemized, InitialSpacer, Unsupported;
    }

    private static bool ReadScheduleBindingField(ScheduleRowBindings bindings, Cursor cur, TfField field, List<(ushort, uint)> queue)
    {
        switch (field.Name)
        {
            case "m_pFields": bindings.FieldObjects = Pointers(); return true;
            case "m_sortFieldArr": bindings.SortObjects = Pointers(); return true;
            case "m_filterArr": bindings.FilterObjects = Pointers(); return true;
            case "m_fieldIds":
                uint count = ReadU32(cur);
                if (count != uint.MaxValue)
                {
                    if (count > 100_000) throw Fail;
                    for (uint i = 0; i < count; i++) bindings.FieldIds.Add(ReadI32(cur));
                }
                return true;
            case "m_bInstanceSchedule": bindings.Itemized = Flag(); return true;
            case "m_showInitialSpacer": bindings.InitialSpacer = Flag(); return true;
            case "m_bRefTable" or "m_bMaterialSchedule" or "m_bFilterBySheet" or "m_bIncludeLinkedFiles"
                or "m_showGrandTotal" or "m_isEmbedded":
                bindings.Unsupported |= Flag();
                return true;
            default: return false;
        }

        List<int> Pointers() => TryReadPtrArray(cur, queue, field with { Mods = 1 }, out var indices, out _)
            ? indices : throw Fail;

        bool Flag() => TryReadFlag(cur, field, out bool value) ? value : throw Fail;
    }
}
