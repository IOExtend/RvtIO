using RvtIO.Model;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    /// <summary>The filter of the elements that do not exist in the export phase, null when there is none to apply: the rule of
    /// <see cref="ProductRules.BuildExportPhaseFilter"/> for <see cref="ExportPhaseId"/>, with the last project phase as the default of the
    /// current profile unless <see cref="ExportAllPhases"/> is set.</summary>
    private ExportPhaseFilter? BuildExportPhaseFilter(RevitFileInfo info)
    {
        int? phaseId = _resolvedConfig.PhaseScope == IfcPhaseScope.SpecificPhase
            ? _resolvedConfig.ExportPhaseId
            : ExportPhaseId;
        bool defaultToLast = _resolvedConfig.PhaseScope switch
        {
            IfcPhaseScope.LastPhase => true,
            IfcPhaseScope.AllPhases => false,
            IfcPhaseScope.SpecificPhase => false,
            _ => CurrentProfile && !ExportAllPhases,
        };
        return ProductRules.BuildExportPhaseFilter(info, phaseId, defaultToLast);
    }
}
