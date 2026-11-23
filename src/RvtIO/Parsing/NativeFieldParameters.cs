using RvtIO.Model;

namespace RvtIO.Parsing;

/// <summary>Built-in parameters stored verbatim in a schema field of the element object.
/// Each row equals the public API value on every sampled element of Revit 2022 projects.
/// Domains (when not empty) restricts a row to the classes where it holds, so a field does not add the parameter to
/// elements that do not have it: a root class or "&lt;root class&gt;/&lt;category id&gt;".</summary>
internal static class NativeFieldParameters
{
    /// UnsetDomains: the same for a field that is "not set" (ElementId -1, double >= 1e29), which creates the parameter
    /// with that sentinel value only where the API shows it; null (rows accepted earlier): wherever the field exists.
    internal readonly record struct Entry(int ParamId, StorageType Storage, string Key, string[] Domains, string[]? UnsetDomains);

    internal static readonly Entry[] All =
    [
        new(-1154689, StorageType.Double, "3471:m_dOutdoorAirflow", ["3471/-2003600"], null), // Outdoor Airflow
        new(-1154671, StorageType.Double, "2136:m_dAirChangesPerHour", ["2135", "2138"], null), // Air Changes per Hour
        new(-1154668, StorageType.Double, "2136:m_dOutdoorAirPerArea", ["2135", "2138"], null), // Outdoor Air per Area
        new(-1154665, StorageType.Double, "2136:m_dOutdoorAirPerPerson", ["2135", "2138"], null), // Outdoor Air per Person
        new(-1154614, StorageType.String, "1506:m_numberingPartition", ["2936", "3366"], null), // Partition
        new(-1154614, StorageType.String, "2150:m_numberingPartition", ["3366"], null), // Partition
        new(-1152375, StorageType.Integer, "1773:m_roundConnDimType", ["1772/-2000011", "1772/-2000150", "1772/-2000151", "1772/-2000196", "1772/-2000576", "1772/-2000700", "1772/-2002000", "1772/-2008143", "1772/-2009030", "1772/None"], null), // Round Connector Dimension
        new(-1152365, StorageType.Double, "2080:m_zOffsets@0", ["1794"], null), // z Offset Value
        new(-1152364, StorageType.Integer, "2080:m_zJustification@0", ["1794"], null), // z Justification
        new(-1152363, StorageType.Double, "2080:m_yOffsets@0", ["1794"], null), // y Offset Value
        new(-1152362, StorageType.Integer, "2080:m_yJustification@0", ["1794"], null), // y Justification
        new(-1152361, StorageType.Integer, "2080:m_yzJustification", ["1794"], null), // yz Justification
        new(-1152360, StorageType.Double, "2080:m_cutbacks@1", ["1794/-2001320"], null), // End Join Cutback
        new(-1152359, StorageType.Double, "2080:m_cutbacks@0", ["1794/-2001320"], null), // Start Join Cutback
        new(-1152358, StorageType.Double, "2080:m_extensions@1", ["1794/-2001320"], null), // End Extension
        new(-1152357, StorageType.Double, "2080:m_extensions@0", ["1794/-2001320"], null), // Start Extension
        new(-1152344, StorageType.Integer, "1024:m_excluded", ["1024"], null), // Excluded
        new(-1152335, StorageType.ElementId, "37:m_assocLevelId", ["1024"], null), // Base Level
        new(-1152177, StorageType.Integer, "3718:m_riserIsSlanted", ["3718"], null), // Slanted
        new(-1152176, StorageType.Integer, "3718:m_hasRiser", ["3718"], null), // Riser
        new(-1152161, StorageType.Integer, "3718:m_riserTreadConnection", ["3718"], null), // Riser To Tread Connection
        new(-1152154, StorageType.Double, "3718:m_nosingLength", ["3718"], null), // Nosing Length
        new(-1152151, StorageType.Integer, "3718:m_hasTread", ["3718"], null), // Tread
        new(-1151811, StorageType.Integer, "3736:m_flipSectionProfile", ["3736/-2000123"], null), // Flip Section Profile
        new(-1151810, StorageType.Double, "3736:m_structuralDepthOnLanding", ["3736"], null), // Structural Depth On Landing
        new(-1151809, StorageType.Double, "3736:m_structuralDepthOnRun", ["3736"], null), // Structural Depth On Run
        new(-1151807, StorageType.Double, "3736:m_width", ["3736"], null), // Width
        new(-1151806, StorageType.Double, "3736:m_totalDepth", ["3736"], null), // Total Depth
        new(-1151708, StorageType.Integer, "3727:m_trimSupportAtUpper", ["3727"], null), // Trim Support at Upper
        new(-1151706, StorageType.Integer, "3727:m_upperEndCut", ["3727"], null), // Upper End Cut
        new(-1151705, StorageType.Integer, "3727:m_lowerEndCut", ["3727"], null), // Lower End Cut
        new(-1151604, StorageType.Integer, "3697:m_useSameTriserAsRun", ["3697"], null), // Same as Run
        new(-1151236, StorageType.Double, "3742:m_leftSupportLateralOffset", ["3742"], null), // Left Lateral Offset
        new(-1151235, StorageType.Double, "3742:m_rightSupportLateralOffset", ["3742"], null), // Right Lateral Offset
        new(-1151234, StorageType.ElementId, "3742:m_cutMarkTypeId", ["3742"], null), // Cut Mark Type
        new(-1151232, StorageType.Integer, "3742:m_rightSupportStyle", ["3742"], null), // Right Support
        new(-1151231, StorageType.Integer, "3742:m_leftSupportStyle", ["3742"], null), // Left Support
        new(-1151216, StorageType.Double, "3742:m_minRunWidth", ["3742"], null), // Minimum Run Width
        new(-1151208, StorageType.ElementId, "3742:m_landingTypeId", ["3742"], null), // Landing Type
        new(-1151207, StorageType.ElementId, "3742:m_runTypeId", ["3742"], null), // Run Type
        new(-1150435, StorageType.String, "1219:m_sFreeSize", ["3148", "3177"], null), // Free Size
        new(-1150435, StorageType.String, "1286:m_sFreeSize", ["1794/-2008010", "1794/-2008016"], null), // Free Size
        new(-1150434, StorageType.String, "1219:m_sOverallSize", ["3148", "3177"], null), // Overall Size
        new(-1150434, StorageType.String, "1286:m_sOverallSize", ["1794/-2008010", "1794/-2008016", "1794/-2008049", "1794/-2008055"], null), // Overall Size
        new(-1150434, StorageType.String, "2964:m_sOverallSize", ["3210"], null), // Overall Size
        new(-1150433, StorageType.Double, "1219:m_dLiningThickness", ["3148", "3177"], null), // Lining Thickness
        new(-1150431, StorageType.Double, "1219:m_dInsulationThickness", ["3148", "3177"], null), // Insulation Thickness
        new(-1150431, StorageType.Double, "2964:m_dInsulationThickness", ["3210"], null), // Insulation Thickness
        new(-1150411, StorageType.Integer, "3189:m_RiseDropSettings", ["3189"], null), // Rise / Drop Symbol
        new(-1150410, StorageType.Integer, "3229:m_TwoLineDropType", ["3229"], null), // Two Line Drop Symbol
        new(-1150361, StorageType.Integer, "835:m_plusOneTreadDepth", ["2143", "3990"], null), // Plus Tread Depth
        // End/Top and Beginning/Bottom extension: one field each (the two ids must not map to both fields).
        new(-1150350, StorageType.Double, "835:m_endExtensionLength", ["2143", "3990"], null), // Length
        new(-1150348, StorageType.Double, "835:m_startExtensionLength", ["2143", "3990"], null), // Length
        new(-1150343, StorageType.Integer, "835:m_transitionType", ["2143", "3990"], null), // Transitions
        new(-1150339, StorageType.ElementId, "835:m_profileId", ["2143", "3990"], null), // Profile
        new(-1150338, StorageType.Double, "835:m_filletRadius", ["2143", "3990"], null), // Fillet Radius
        new(-1150337, StorageType.Integer, "835:m_defaultContinuousRailJoinType", ["2143", "3990"], null), // Default Join
        new(-1150231, StorageType.Integer, "3928:m_leaderRightAttachment", ["3971"], null), // Right Attachment
        new(-1150230, StorageType.Integer, "3928:m_leaderLeftAttachment", ["3971"], null), // Left Attachment
        new(-1150215, StorageType.Integer, "705:m_attachmentType@1", ["1794/-2001320"], null), // End Attachment Type
        new(-1150214, StorageType.Integer, "705:m_attachmentType@0", ["1794/-2001320"], null), // Start Attachment Type
        new(-1150188, StorageType.Double, "2136:m_dPlenumLighting", ["2135", "2138"], null), // Plenum Lighting Contribution
        new(-1150170, StorageType.Double, "1005:m_columnSymbolElev", ["1005/-2000279"], null), // Column Symbolic Offset
        new(-1144331, StorageType.Double, "3471:m_luminairePlane", ["3471"], null), // Lighting Calculation Luminaire Plane
        new(-1140757, StorageType.ElementId, "2264:m_viewId", ["2264"], null), // Tag new members in view
        new(-1140500, StorageType.Integer, "550:m_type", ["550"], null), // Boundary Conditions Type
        new(-1140422, StorageType.String, "2960:m_grade", ["2486"], null), // Keynote
        new(-1140363, StorageType.ElementId, "1064:m_categoryId", ["1064"], null), // Category
        new(-1140363, StorageType.ElementId, "2209:m_categoryId", ["2471", "3623"], null), // Category
        new(-1140363, StorageType.ElementId, "2628:m_categoryId", ["2628"], null), // Category
        new(-1140363, StorageType.ElementId, "743:m_massSurfaceSubCategoryId", ["743"], null), // Category
        new(-1140362, StorageType.ElementId, "1064:m_categoryId", ["1064"], null), // Category
        new(-1140362, StorageType.ElementId, "2209:m_categoryId", ["2471", "3623"], null), // Category
        new(-1140362, StorageType.ElementId, "2628:m_categoryId", ["2628"], null), // Category
        new(-1140362, StorageType.ElementId, "743:m_massSurfaceSubCategoryId", ["743"], null), // Category
        new(-1140355, StorageType.String, "2479:m_name", ["2486", "29"], null), // Name
        new(-1140336, StorageType.Integer, "3190:m_calculationLevel", ["3189"], null), // Calculations
        new(-1140335, StorageType.Integer, "3190:m_calculationLevel", ["3229/-2008043"], null), // Calculations
        new(-1140333, StorageType.ElementId, "1219:m_idSystemType", ["3177"], null), // System Type
        new(-1140332, StorageType.String, "3190:m_abbreviation", ["3189", "3229"], null), // Abbreviation
        new(-1140328, StorageType.Integer, "3229:m_flowConversionMethod", ["3229"], null), // Flow Conversion Method
        new(-1140327, StorageType.Integer, "3170:m_numberOfElements", ["3228"], null), // Number of Elements
        new(-1140324, StorageType.String, "3170:m_strName", ["3188", "3228"], null), // System Name
        new(-1140284, StorageType.Double, "2366:m_widthOrDiameter", ["1794/-2008055"], null), // Maximum Size
        new(-1140283, StorageType.Double, "2366:m_widthOrDiameter", ["1794/-2008055"], null), // Minimum Size
        new(-1140277, StorageType.ElementId, "602:m_idSegment", ["3210"], null), // Pipe Segment
        new(-1140238, StorageType.Double, "2964:m_dOuterDiameter", ["3210"], null), // Outside Diameter
        new(-1140226, StorageType.Double, "2964:m_dAdditionalFlow", ["3210/-2008044"], null), // Additional Flow
        new(-1140225, StorageType.Double, "602:m_dWidthOrDiameter", ["3210"], null), // Diameter
        new(-1140218, StorageType.ElementId, "3229:m_fluidType", ["3229/-2008043"], null), // Fluid Type
        new(-1140217, StorageType.Double, "3229:m_fluidTemperature", ["3229/-2008043"], null), // Fluid Temperature
        new(-1140213, StorageType.Double, "2382:m_dActualFlow", ["1794/-2008099"], null), // Flow
        new(-1140213, StorageType.Double, "2382:m_dFlow", ["1794/-2008099"], null), // Flow
        new(-1140213, StorageType.Double, "2964:m_dFlow", ["3210/-2008044"], null), // Flow
        new(-1140212, StorageType.Double, "2371:m_innerDiameter", ["3210"], null), // Inside Diameter
        new(-1140212, StorageType.Double, "2964:m_dInnerDiameter", ["3210"], null), // Inside Diameter
        new(-1140211, StorageType.Double, "2964:m_dReynoldsNumber", ["3210/-2008044"], null), // Reynolds Number
        new(-1140210, StorageType.Double, "2964:m_dRelativeRoughness", ["3210"], null), // Relative Roughness
        new(-1140209, StorageType.Integer, "2964:m_kFlowState", ["3210/-2008044"], null), // Flow State
        new(-1140208, StorageType.Double, "2964:m_dFrictionFactor", ["3210/-2008044"], null), // Friction Factor
        new(-1140207, StorageType.Double, "2964:m_dVelocity", ["3210/-2008044"], null), // Velocity
        new(-1140206, StorageType.Double, "2964:m_dFriction", ["3210/-2008044"], null), // Friction
        new(-1140205, StorageType.Double, "2964:m_dPressureDrop", ["3210/-2008044"], null), // Pressure Drop
        new(-1140204, StorageType.Double, "2371:m_roughness", ["3210"], null), // Roughness
        new(-1140204, StorageType.Double, "3181:m_dRoughness", ["3181"], null), // Roughness
        new(-1140127, StorageType.Double, "749:m_dOuterDiameter", ["3134"], null), // Outside Diameter
        new(-1140126, StorageType.Double, "749:m_dInnerDiameter", ["3134"], null), // Inside Diameter
        new(-1140123, StorageType.Double, "602:m_dWidthOrDiameter", ["3134"], null), // Diameter(Trade Size)
        new(-1140122, StorageType.Double, "602:m_dWidthOrDiameter", ["598"], null), // Width
        new(-1140121, StorageType.Double, "602:m_dHeight", ["598"], null), // Height
        new(-1140118, StorageType.ElementId, "3135:m_idStandardType", ["3135"], null), // Standard
        new(-1140112, StorageType.Double, "598:m_rungSpace", ["598"], null), // Rung Space
        new(-1140106, StorageType.Integer, "3253:m_eTickMarkState", ["3253"], null), // Tick Marks
        new(-1140097, StorageType.Integer, "3253:m_eType", ["3253/-2008039"], null), // Type
        new(-1140091, StorageType.Integer, "3272:m_bShareNeutral", ["3272"], null), // Share Neutral Conductor
        new(-1140090, StorageType.Integer, "3272:m_bShareGround", ["3272"], null), // Share Ground Conductor
        new(-1140061, StorageType.Integer, "3146:m_kPhase", ["3146"], null), // Phase
        new(-1140059, StorageType.Integer, "3146:m_iNumWires", ["3146"], null), // Wires
        new(-1140058, StorageType.Double, "3249:m_dActualVoltage", ["3249"], null), // Actual Voltage
        new(-1140057, StorageType.Double, "3249:m_dMinVoltage", ["3249"], null), // Minimum Voltage
        new(-1140056, StorageType.Double, "3249:m_dMaxVoltage", ["3249"], null), // Maximum Voltage
        new(-1140035, StorageType.Double, "3471:m_dRoomCavityRatio", ["3471/-2003600"], null), // Room Cavity Ratio
        new(-1140033, StorageType.Double, "3471:m_dEstimatedIllumination", ["3471/-2003600"], null), // Average Estimated Illumination
        new(-1140028, StorageType.ElementId, "3272:m_idMaterial", ["3272"], null), // Material
        new(-1140026, StorageType.ElementId, "3272:m_idInsulation", ["3272"], null), // Insulation
        new(-1140024, StorageType.Double, "3272:m_dNeutralMultiplier", ["3272"], null), // Neutral Multiplier
        new(-1140023, StorageType.Integer, "3272:m_bNeutralIncludedInBalancedLoad", ["3272"], null), // Neutral Included in Balanced Load
        new(-1133501, StorageType.Double, "1322:m_offsetFromLevel", ["1322/-2000095"], null), // Origin Level Offset
        new(-1133500, StorageType.ElementId, "37:m_assocLevelId", ["1322"], null), // Reference Level
        new(-1114711, StorageType.Double, "2136:m_dehumidificationSetPoint", ["2135", "2138"], null), // Dehumidification Set Point
        new(-1114710, StorageType.Double, "2136:m_humidificationSetPoint", ["2135", "2138"], null), // Humidification Set Point
        new(-1114709, StorageType.Double, "2136:m_coolingSetPoint", ["2135", "2138"], null), // Cooling Set Point
        new(-1114708, StorageType.Double, "2136:m_heatingSetPoint", ["2135", "2138"], null), // Heating Set Point
        new(-1114399, StorageType.Integer, "1773:m_hasRoomCalculationPoint", ["1772/-2000011", "1772/-2000151", "1772/-2000196", "1772/-2000576", "1772/-2000700", "1772/-2002000", "1772/-2008143", "1772/-2009030", "1772/None"], null), // Room Calculation Point
        new(-1114393, StorageType.ElementId, "104:m_idDefaultElbow", ["3130"], null), // Horizontal Bend
        new(-1114388, StorageType.ElementId, "104:m_idDefaultElbowDown", ["3130"], null), // Vertical Outside Bend
        new(-1114387, StorageType.ElementId, "104:m_idDefaultElbowUp", ["3130"], null), // Vertical Inside Bend
        new(-1114357, StorageType.Double, "2135:m_dCoolingSetPoint", ["2135"], null), // Unoccupied Cooling Set Point
        new(-1114356, StorageType.String, "2135:m_strEquipmentEndTime", ["2135"], null), // Closing Time
        new(-1114355, StorageType.String, "2135:m_strEquipmentStartTime", ["2135"], null), // Opening Time
        new(-1114351, StorageType.ElementId, "2136:m_idPowerSchedule", ["2135", "2138"], null), // Power Schedule
        new(-1114350, StorageType.ElementId, "2136:m_idLightingSchedule", ["2135", "2138"], null), // Lighting Schedule
        new(-1114349, StorageType.ElementId, "2136:m_idOccupancySchedule", ["2135", "2138"], null), // Occupancy Schedule
        new(-1114348, StorageType.Double, "2136:m_dInfiltration", ["2135", "2138"], null), // Infiltration Airflow per area
        new(-1114344, StorageType.Double, "4215:m_dCoilBypassPercentage", ["4215"], null), // Coil Bypass
        new(-1114341, StorageType.Integer, "4215:m_bUseAirChangesPerHour", ["4215"], null), // Use Air Changes Per Hour
        new(-1114338, StorageType.Integer, "4215:m_bUseDehumidificationSetPoint", ["4215"], null), // Use Dehumidification Set Point
        new(-1114330, StorageType.Integer, "3471:m_bPlenum", ["3471/-2003600"], null), // Plenum
        new(-1114329, StorageType.Integer, "3471:m_bOccupiable", ["3471/-2003600"], null), // Occupiable
        new(-1114326, StorageType.ElementId, "4215:m_phaseId", ["4215"], null), // Phase
        new(-1114325, StorageType.ElementId, "4215:m_phaseId", ["4215"], null), // Phase Id
        new(-1114323, StorageType.Double, "3471:m_dCalculatedSupplyAirflow", ["3471/-2000160"], null), // Calculated Supply Airflow per area
        new(-1114322, StorageType.Double, "3471:m_dCalculatedCoolingLoad", ["3471/-2000160"], null), // Calculated Cooling Load per area
        new(-1114321, StorageType.Double, "3471:m_dCalculatedHeatingLoad", ["3471/-2000160"], null), // Calculated Heating Load per area
        new(-1114317, StorageType.ElementId, "37:m_assocLevelId", ["4215"], null), // Level
        new(-1114313, StorageType.Double, "4215:m_dDehumidificationSetPoint", ["4215"], null), // Dehumidification Set Point
        new(-1114311, StorageType.Double, "4215:m_dCoolingAirTemperature", ["4215"], null), // Cooling Air Temperature
        new(-1114310, StorageType.Double, "4215:m_dHeatingAirTemperature", ["4215"], null), // Heating Air Temperature
        new(-1114309, StorageType.Double, "4215:m_dCoolingSetPoint", ["4215"], null), // Cooling Set Point
        new(-1114308, StorageType.Double, "4215:m_dHeatingSetPoint", ["4215"], null), // Heating Set Point
        new(-1114307, StorageType.Double, "4215:m_dCalculatedSupplyAirflow", ["4215"], null), // Calculated Supply Airflow
        new(-1114306, StorageType.Double, "4215:m_dCalculatedCoolingLoad", ["4215"], null), // Calculated Cooling Load
        new(-1114305, StorageType.Double, "4215:m_dCalculatedHeatingLoad", ["4215"], null), // Calculated Heating Load
        new(-1114304, StorageType.Integer, "4215:m_eServiceType", ["4215"], null), // Service Type
        new(-1114302, StorageType.Double, "4215:m_perimeter", ["4215"], null), // Perimeter
        new(-1114261, StorageType.Double, "3471:m_dActualLightingLoad", ["3471/-2000160"], null), // Actual Lighting Load per area
        new(-1114260, StorageType.Double, "3471:m_dActualPowerLoad", ["3471/-2000160"], null), // Actual Power Load per area
        new(-1114256, StorageType.Double, "3471:m_dDesignCoolingLoad", ["3471/-2003600"], null), // Design Cooling Load
        new(-1114255, StorageType.Double, "3471:m_dCalculatedCoolingLoad", ["3471/-2003600"], null), // Calculated Cooling Load
        new(-1114254, StorageType.Double, "3471:m_dDesignHeatingLoad", ["3471/-2003600"], null), // Design Heating Load
        new(-1114253, StorageType.Double, "3471:m_dCalculatedHeatingLoad", ["3471/-2003600"], null), // Calculated Heating Load
        new(-1114252, StorageType.Integer, "3471:m_dDesignReturnAirflow", ["3471/-2003600"], null), // Return Airflow
        new(-1114252, StorageType.Integer, "3471:m_eBaseReturnAirflowOn", ["3471/-2003600"], null), // Return Airflow
        new(-1114243, StorageType.Integer, "1773:m_bElecKeepAnnotationOrientation", ["1772/-2000011", "1772/-2000151", "1772/-2000196", "1772/-2000576", "1772/-2000700", "1772/-2002000", "1772/-2008143", "1772/-2009030", "1772/None"], null), // Maintain Annotation Orientation
        new(-1114240, StorageType.String, "1219:m_sCalculatedSize", ["3148"], null), // Size
        new(-1114240, StorageType.String, "1286:m_sCalculatedSize", ["1794/-2008010", "1794/-2008013", "1794/-2008016", "1794/-2008049", "1794/-2008055", "1794/-2008126", "1794/-2008128"], null), // Size
        new(-1114240, StorageType.String, "1286:m_sFreeSize", ["1794/-2008016"], null), // Size
        new(-1114240, StorageType.String, "1286:m_sOverallSize", ["1794/-2008016", "1794/-2008049", "1794/-2008055"], null), // Size
        new(-1114240, StorageType.String, "2964:m_sCalculatedSize", ["3210"], null), // Size
        new(-1114240, StorageType.String, "2964:m_sOverallSize", ["3210"], null), // Size
        new(-1114240, StorageType.String, "598:m_calculatedSize", ["598"], null), // Size
        new(-1114240, StorageType.String, "749:m_sCalculatedSize", ["3134"], null), // Size
        new(-1114239, StorageType.Double, "2136:m_dSensibleHeatGainPerPerson", ["2135", "2138"], null), // Sensible Heat Gain per person
        new(-1114226, StorageType.Double, "3471:m_dActualLightingLoad", ["3471/-2000160", "3471/-2003600"], null), // Actual Lighting Load
        new(-1114225, StorageType.Double, "3471:m_dActualPowerLoad", ["3471/-2000160", "3471/-2003600"], null), // Actual Power Load
        new(-1114224, StorageType.Integer, "3471:m_eBaseLightingLoadOn", ["3471"], null), // Base Lighting Load on
        new(-1114223, StorageType.Integer, "3471:m_eBasePowerLoadOn", ["3471"], null), // Base Power Load on
        new(-1114222, StorageType.Double, "3471:m_dDesignOtherLoadPerArea", ["3471/-2003600"], null), // Design Other Load per area
        new(-1114220, StorageType.Double, "2136:m_LightingLoadDensity", ["2135", "2138"], null), // Specified Lighting Load per area
        new(-1114219, StorageType.Double, "2136:m_PowerLoadDensity", ["2135", "2138"], null), // Specified Power Load per area
        new(-1114206, StorageType.Integer, "1773:m_partType", ["1772/-2000011", "1772/-2000150", "1772/-2000151", "1772/-2000196", "1772/-2000576", "1772/-2000700", "1772/-2002000", "1772/-2008143", "1772/-2009030", "1772/None"], null), // Part Type
        new(-1114196, StorageType.Double, "3471:m_dActualExhaustAirflow", ["3471/-2003600"], null), // Actual Exhaust Airflow
        new(-1114195, StorageType.Double, "3471:m_dActualReturnAirflow", ["3471/-2003600"], null), // Actual Return Airflow
        new(-1114194, StorageType.Double, "3471:m_dActualSupplyAirflow", ["3471/-2003600"], null), // Actual Supply Airflow
        new(-1114189, StorageType.Double, "2136:m_dLatentHeatGainPerPerson", ["2135", "2138"], null), // Latent Heat Gain per person
        new(-1114180, StorageType.Double, "3471:m_dCalculatedSupplyAirflow", ["3471/-2003600"], null), // Calculated Supply Airflow
        new(-1114176, StorageType.Double, "3471:m_dDesignSupplyAirflow", ["3471/-2003600"], null), // Specified Supply Airflow
        new(-1114175, StorageType.Double, "2136:m_dAreaPerPerson", ["2135", "2138"], null), // Area per Person
        new(-1114172, StorageType.ElementId, "3471:m_gbxmlSpaceTypeId", ["3471"], null), // Space Type
        new(-1114167, StorageType.Integer, "1219:m_bSizeLock", ["3148", "3177"], null), // Size Lock
        new(-1114166, StorageType.Double, "1219:m_dAdditionalFlow", ["3148", "3177"], null), // Additional Flow
        new(-1114132, StorageType.Double, "602:m_dOffsetStart", ["3134", "3148", "598"], null), // Middle Elevation
        new(-1114131, StorageType.ElementId, "104:m_idDefaultUnion", ["114", "3130", "3135", "3181"], null), // Union
        new(-1114129, StorageType.Double, "1219:m_dHydraulicDiameter", ["3148", "3177"], null), // Hydraulic Diameter
        new(-1114128, StorageType.Double, "1219:m_dReynoldsNumber", ["3148", "3177"], null), // Reynolds number
        new(-1114127, StorageType.Double, "1219:m_dEqDiameter", ["3148", "3177"], null), // Equivalent Diameter
        new(-1114124, StorageType.Double, "1219:m_dLossCoefficient", ["3177"], null), // Loss Coefficient
        new(-1114121, StorageType.Double, "1219:m_dVelocityPressure", ["3148", "3177"], null), // Velocity Pressure
        new(-1114116, StorageType.Double, "1219:m_dFriction", ["3148", "3177"], null), // Friction
        new(-1114114, StorageType.Double, "104:m_dRoughness", ["110", "114"], null), // Roughness
        new(-1114114, StorageType.Double, "3159:m_roughness", ["3159"], null), // Roughness
        new(-1114113, StorageType.ElementId, "104:m_idDefaultTransition", ["114", "3130", "3135", "3181"], null), // Transition
        new(-1114112, StorageType.ElementId, "104:m_idDefaultCross", ["3130", "3135"], null), // Cross
        new(-1114111, StorageType.ElementId, "104:m_idDefaultTee", ["114", "3130", "3135", "3181"], null), // Tee
        new(-1114108, StorageType.Double, "1219:m_dPressureDrop", ["3177"], null), // Pressure Drop
        new(-1114107, StorageType.Double, "1219:m_dVelocity", ["3148", "3177"], null), // Velocity
        new(-1114106, StorageType.Integer, "602:m_eVertOffset", ["3134", "3148", "3210", "598"], null), // Vertical Justification
        new(-1114105, StorageType.Integer, "602:m_eHorOffset", ["3134", "3148", "3210", "598"], null), // Horizontal Justification
        new(-1114103, StorageType.Double, "1219:m_dEqDiameter", ["3148/-2008000", "3177"], null), // Diameter
        new(-1114103, StorageType.Double, "1219:m_dHydraulicDiameter", ["3148/-2008000", "3177"], null), // Diameter
        new(-1114103, StorageType.Double, "602:m_dWidthOrDiameter", ["3148/-2008000", "3177"], null), // Diameter
        new(-1114102, StorageType.Double, "1219:m_dFreeHeight", ["3148/-2008000"], null), // Height
        new(-1114102, StorageType.Double, "602:m_dHeight", ["3148/-2008000"], null), // Height
        new(-1114101, StorageType.Double, "1219:m_dFreeWidth", ["3148/-2008000"], null), // Width
        new(-1114101, StorageType.Double, "602:m_dWidthOrDiameter", ["3148/-2008000"], null), // Width
        new(-1114005, StorageType.Integer, "3173:m_pattern", ["3177"], null), // Flex Pattern
        new(-1114003, StorageType.Double, "602:m_dOffsetEnd", ["3134", "3148", "3210", "598"], null), // End Middle Elevation
        new(-1114002, StorageType.Double, "602:m_dOffsetStart", ["3134", "3148", "3177", "3210", "598"], null), // Start Middle Elevation
        new(-1114000, StorageType.ElementId, "602:m_idLevelStart", ["3134", "3148", "3177", "3210", "598"], null), // Reference Level
        new(-1019100, StorageType.Integer, "362:m_wallCrossSection", ["3524"], null), // Cross-Section
        new(-1019100, StorageType.Integer, "500:m_generatedWallCrossSection", ["3524"], null), // Cross-Section
        new(-1018850, StorageType.String, "3905:m_name", ["3780"], null), // Name
        new(-1018800, StorageType.Integer, "1773:m_eStructConnectionApplyTo", ["1772/-2000700", "1772/None"], null), // Apply to
        new(-1018503, StorageType.Double, "2937:m_totalVolume", ["2936", "3366"], null), // Reinforcement Volume
        new(-1018502, StorageType.Double, "3377:m_barVolume", ["1794", "1913", "3524", "804"], null), // Estimated Reinforcement Volume
        new(-1018274, StorageType.Integer, "3281:m_deformationType", ["3281"], null), // Deformation
        new(-1018001, StorageType.Integer, "2936:m_ruleType", ["2936"], null), // Layout Rule
        new(-1017032, StorageType.String, "3277:m_schedMark", ["3277"], null), // Schedule Mark
        new(-1017032, StorageType.String, "3309:m_scheduleMark", ["3309"], null), // Schedule Mark
        new(-1015005, StorageType.Integer, "381:m_isReaction", ["2320", "380"], null), // Is Reaction
        new(-1015000, StorageType.ElementId, "381:m_loadCaseId", ["2320", "380"], null), // Load Case
        new(-1013434, StorageType.Double, "880:m_length", ["880"], null), // Length
        new(-1013433, StorageType.String, "3905:m_name", ["880"], null), // Name
        new(-1013431, StorageType.Double, "2264:m_spacing", ["2264/-2001327"], null), // Fixed Spacing
        new(-1013430, StorageType.Double, "2264:m_maximumSpacing", ["2264/-2001327"], null), // Maximum Spacing
        new(-1013427, StorageType.Integer, "2264:m_b3D", ["2264"], null), // 3D
        new(-1013419, StorageType.ElementId, "2264:m_newBeamType", ["2264"], null), // Beam Type (No Family Name)
        new(-1013412, StorageType.Double, "918:m_offset", ["838/-2000045"], null), // Wall offset
        new(-1013411, StorageType.ElementId, "2264:m_newBeamType", ["2264"], null), // Beam Type
        new(-1013410, StorageType.Integer, "2264:m_layoutRule", ["2264"], null), // Layout Rule
        new(-1013409, StorageType.Integer, "2264:m_justification", ["2264"], null), // Justification
        new(-1013408, StorageType.Double, "2264:m_spacing", ["2264"], null), // Centerline Spacing
        new(-1013405, StorageType.Double, "1219:m_dFlow", ["3148", "3177"], null), // Flow
        new(-1013405, StorageType.Double, "2381:m_dFlow", ["1794/-2008013"], null), // Flow
        new(-1013339, StorageType.Double, "1182:m_uConstAngle", ["1182"], null), // Angle
        new(-1013310, StorageType.Double, "369#0:m_angle", ["3524/-2000011"], null), // Angle
        new(-1013310, StorageType.Double, "369#1:m_angle", ["3524/-2000011"], null), // Angle
        new(-1013310, StorageType.Double, "369:m_angle", ["3524/-2000011"], null), // Angle
        new(-1013309, StorageType.Double, "369#0:m_angle", ["3524/-2000011"], null), // Angle
        new(-1013309, StorageType.Double, "369#1:m_angle", ["3524/-2000011"], null), // Angle
        new(-1013309, StorageType.Double, "369:m_angle", ["3524/-2000011"], null), // Angle
        new(-1013307, StorageType.Integer, "369#0:m_fixedNum", ["3524/-2000011"], null), // Number
        new(-1013306, StorageType.Integer, "369:m_fixedNum", ["3524/-2000011"], null), // Number
        new(-1013302, StorageType.Double, "1169:m_spacing", ["2559"], null), // Spacing
        new(-1013300, StorageType.Integer, "1169:m_layoutType", ["2559"], null), // Layout
        new(-1012843, StorageType.Integer, "1773:m_bIsHostBased", ["1772/-2000045", "1772/-2000150", "1772/-2000196", "1772/-2001300", "1772/-2003000", "1772/-2003400", "1772/-2003404", "1772/-2008143", "1772/-2009000"], null), // Host
        new(-1012843, StorageType.Integer, "1773:m_canHostRebar", ["1772/-2000045", "1772/-2000150", "1772/-2000196", "1772/-2001300", "1772/-2003000", "1772/-2003400", "1772/-2003404", "1772/-2008143", "1772/-2009000"], null), // Host
        new(-1012841, StorageType.Integer, "1773:m_bUsePreCutShape", ["1772/-2000035", "1772/-2000700", "1772/-2002000"], null), // Show family pre-cut in plan views
        new(-1012840, StorageType.Double, "871:m_defaultSetback", ["3440", "873"], null), // Default Setback
        new(-1012839, StorageType.Integer, "871:m_cutsWall", ["873"], null), // Cuts Wall
        new(-1012838, StorageType.Integer, "871:m_cutByInserts", ["873"], null), // Cut by Inserts
        new(-1012834, StorageType.Integer, "1773:m_bShared", ["1772/-2000011", "1772/-2000150", "1772/-2000151", "1772/-2000196", "1772/-2000510", "1772/-2000576", "1772/-2000700", "1772/-2001300", "1772/-2002000", "1772/-2008143", "1772/-2009030", "1772/None"], null), // Shared
        new(-1012833, StorageType.Integer, "1773:m_isWorkPlaneBased", ["1772/-2000011", "1772/-2000150", "1772/-2000151", "1772/-2000196", "1772/-2000576", "1772/-2000700", "1772/-2001300", "1772/-2008143", "1772/-2009000", "1772/None"], null), // Work Plane-Based
        new(-1012830, StorageType.Integer, "1773:m_keepTextReadable", ["1772/-2000150", "1772/-2000700", "1772/None"], null), // Keep text readable
        new(-1012811, StorageType.Integer, "1773:m_bAllowCutWithUnattachedVoids", ["1772/-2000011", "1772/-2000150", "1772/-2000151", "1772/-2000196", "1772/-2000576", "1772/-2000700", "1772/-2001300", "1772/-2002000", "1772/-2008143", "1772/-2009000", "1772/-2009030", "1772/None"], null), // Cut with Voids When Loaded
        new(-1012808, StorageType.Integer, "1773:m_isVertical", ["1772/-2000011", "1772/-2000150", "1772/-2000151", "1772/-2000196", "1772/-2000576", "1772/-2000700", "1772/-2001300", "1772/-2002000", "1772/-2008143", "1772/-2009000", "1772/None"], null), // Always vertical
        new(-1012807, StorageType.Integer, "1773:m_rotateTagTextWithComponent", ["1772/-2000150", "1772/-2000510", "1772/-2000560", "1772/-2000576"], null), // Rotate with component
        new(-1012807, StorageType.Integer, "1773:m_rotateTagWithComponent", ["1772/-2000150", "1772/-2000510", "1772/-2000560", "1772/-2000576", "1772/-2000700", "1772/-2002000", "1772/-2006045", "1772/None"], null), // Rotate with component
        new(-1012806, StorageType.Double, "2446:m_nVolume", ["1794"], null), // Volume
        new(-1012804, StorageType.Double, "4100:m_wallOffset", ["4157"], null), // Offset From Wall
        new(-1012802, StorageType.Double, "4100:m_distance", ["4157/-2000181", "4157/-2000182"], null), // Offset From Level
        new(-1012801, StorageType.ElementId, "37:m_assocLevelId", ["4157"], null), // Level
        new(-1012706, StorageType.Integer, "1773:m_rotateTagTextWithComponent", ["1772/-2000700", "1772/None"], null), // Rotate text with component
        new(-1012705, StorageType.String, "388:m_areaElemName", ["388"], null), // Name
        new(-1012704, StorageType.ElementId, "3471:m_areaSchemeId", ["3471"], null), // Area Scheme Id
        new(-1012110, StorageType.String, "3065:m_name", ["3065"], null), // Name
        new(-1012100, StorageType.ElementId, "37:m_createdPhaseId", ["1024", "1220", "1604", "1794", "1899", "1913", "2264", "2320", "2447", "2610", "267", "272", "273", "280", "281", "284", "285", "2936", "2965", "3054", "3134", "3148", "3177", "3210", "3277", "3309", "3366", "3451", "3524", "3525", "3553", "3568", "3595", "3681", "3727", "380", "3892", "3967", "4012", "4157", "485", "565", "598", "642", "752", "754", "804", "838", "903"], null), // Phase Created
        new(-1012024, StorageType.ElementId, "743:m_materialId", ["743"], null), // Graphical Appearance
        new(-1012007, StorageType.Double, "2446:m_nVolume", ["1794"], null), // Gross Volume
        new(-1012006, StorageType.Double, "2446:m_nSurfaceArea", ["1794"], null), // Gross Surface Area
        new(-1011957, StorageType.Integer, "3066:m_issued", ["3066"], null), // Issued
        new(-1011955, StorageType.String, "3066:m_issuedBy", ["3066"], null), // Issued by
        new(-1011954, StorageType.String, "3066:m_issuedTo", ["3066"], null), // Issued to
        new(-1011953, StorageType.String, "3066:m_date", ["3066"], null), // Revision Date
        new(-1011952, StorageType.String, "3066:m_description", ["3066"], null), // Revision Description
        new(-1011951, StorageType.String, "3066:m_numberStr", ["3066"], null), // Revision Number
        new(-1011950, StorageType.Integer, "3066:m_sequenceNumber", ["3066"], null), // Revision Sequence
        new(-1010304, StorageType.Double, "3912:m_thickness", ["3912"], null), // Thickness
        new(-1010303, StorageType.ElementId, "1795:m_hostId", ["1794/-2000014", "1794/-2000023", "1794/-2000170"], null), // Host Id
        new(-1010303, StorageType.ElementId, "897:m_hostId", ["1794"], null), // Host Id
        new(-1010302, StorageType.Double, "3912:m_offset", ["3912"], null), // Offset
        new(-1010108, StorageType.String, "2960:m_grade", ["2486"], null), // Manufacturer
        new(-1010106, StorageType.String, "266:m_comments", ["267", "272", "273", "280", "281", "284", "285"], null), // Comments
        new(-1010104, StorageType.String, "2960:m_grade", ["2486"], null), // URL
        new(-1009522, StorageType.Integer, "1918:m_alignmentMethod", ["838/-2000045"], null), // Alignment Method
        new(-1009509, StorageType.Integer, "269:m_alignmentMethod@1", ["273"], null), // Top Alignment Method
        new(-1009506, StorageType.Integer, "269:m_alignmentMethod@0", ["273"], null), // Base Alignment Method
        new(-1009503, StorageType.Integer, "269:m_alignmentMethod@1", ["267", "272"], null), // End Alignment Method
        new(-1009500, StorageType.Integer, "269:m_alignmentMethod@0", ["267", "272"], null), // Start Alignment Method
        new(-1008211, StorageType.Integer, "1020:m_defaultTemplateIdAssignOnViewCreation", ["1020/-2000400", "1020/-2000538", "1020/-2000700", "1020/-2006045", "1020/None", "1021"], null), // New views are dependent on template
        new(-1008210, StorageType.ElementId, "1020:m_defaultTemplateId", ["1020"], null), // View Template applied to new views
        new(-1008209, StorageType.Integer, "1020:m_planViewDirection", ["1020/-2000538", "1020/None"], null), // View Direction
        new(-1008207, StorageType.ElementId, "1020:m_elevatnAttrId", ["1020"], null), // Elevation Tag
        new(-1008206, StorageType.ElementId, "1020:m_calloutAttrId", ["1020", "1021"], null), // Callout Tag
        new(-1008205, StorageType.ElementId, "1020:m_sectionAttrId", ["1020"], null), // Section Tag
        new(-1008201, StorageType.Double, "627:m_cornerRadius", ["627"], null), // Corner Radius
        new(-1008200, StorageType.ElementId, "627:m_familyTagId", ["627"], null), // Callout Head
        new(-1008000, StorageType.String, "1052:m_text", ["2108", "2299", "3396"], null), // Name
        new(-1007850, StorageType.String, "3398:m_name", ["3398"], null), // Key Name
        new(-1007408, StorageType.String, "995:m_approvedBy", ["995"], null), // Approved By
        new(-1007407, StorageType.String, "995:m_designedBy", ["995"], null), // Designed By
        new(-1007405, StorageType.String, "995:m_checkedBy", ["995"], null), // Checked By
        new(-1007404, StorageType.String, "995:m_drawnBy", ["995"], null), // Drawn By
        new(-1007401, StorageType.String, "995:m_sheetNumber", ["995"], null), // Sheet Number
        new(-1007400, StorageType.String, "108:m_viewName", ["995"], null), // Sheet Name
        new(-1007395, StorageType.Integer, "513:m_autoJoinCond", ["2559"], null), // Join Condition
        new(-1007389, StorageType.ElementId, "513:m_intMullions@0", ["2559"], null), // Interior Type
        new(-1007388, StorageType.ElementId, "513:m_panel", ["2559"], null), // Curtain Panel
        new(-1007363, StorageType.Double, "3910:m_angle", ["3910/-2000171"], null), // Angle
        new(-1007219, StorageType.Double, "3681:m_topLevelOffset", ["3681"], []), // Top Offset
        new(-1007203, StorageType.Double, "3742:m_minTreadDepth", ["3742"], null), // Minimum Tread Depth
        new(-1007202, StorageType.Double, "3742:m_maxRiserHeight", ["3742"], null), // Maximum Riser Height
        new(-1007201, StorageType.ElementId, "3681:m_topLevelId", ["3681"], ["3681"]), // Top Level
        new(-1007200, StorageType.ElementId, "3681:m_baseLevelId", ["3681"], []), // Base Level
        new(-1007111, StorageType.Integer, "2299:m_isBuildingStory", ["2299"], null), // Building Story
        new(-1007004, StorageType.Integer, "3934:m_orientation", ["3475"], null), // Orientation
        new(-1006939, StorageType.Double, "2299:m_roomComputationElevationOffset", ["2299"], null), // Computation Height
        new(-1006925, StorageType.Double, "3471:m_lowerOffset", ["3471/-2000160", "3471/-2003600"], null), // Base Offset
        new(-1006924, StorageType.Double, "3471:m_upperOffset", ["3471/-2000160", "3471/-2003600"], null), // Limit Offset
        new(-1006922, StorageType.ElementId, "3471:m_upperLevelId", ["3471"], null), // Upper Limit
        new(-1006920, StorageType.Double, "3471:m_height", ["3471/-2000160", "3471/-2003600"], null), // Unbounded Height
        new(-1006916, StorageType.ElementId, "3471:m_levelId", ["3471"], null), // Level
        new(-1006916, StorageType.ElementId, "37:m_assocLevelId", ["3471"], null), // Level
        new(-1006661, StorageType.Integer, "3703:m_fullStepArrow", ["3703"], null), // Full Step Arrow
        new(-1006660, StorageType.Double, "3703:m_startExtension", ["3703"], null), // Start Extension
        new(-1006636, StorageType.Integer, "3702:m_textOrientation", ["3702"], null), // Orientation
        new(-1006635, StorageType.ElementId, "3703:m_textTypeId", ["3703"], null), // Text Type
        new(-1006634, StorageType.String, "3702:m_downText", ["3702/-2000938"], null), // Down Text
        new(-1006633, StorageType.Integer, "3702:m_showDownText", ["3702/-2000938"], null), // Show Down Text
        new(-1006632, StorageType.String, "3702:m_upText", ["3702"], null), // Up Text
        new(-1006631, StorageType.Integer, "3702:m_showUpText", ["3702"], null), // Show Up Text
        new(-1006630, StorageType.Integer, "3703:m_showArrowheadToCutMark", ["3703"], null), // Show Arrowhead to Cut Mark
        new(-1006625, StorageType.Double, "3703:m_distanceToCutMark", ["3703"], null), // Distance to Cut Mark
        new(-1006623, StorageType.ElementId, "3703:m_arrowheadTypeId", ["3703"], null), // Arrowhead Type
        new(-1006621, StorageType.Double, "974:m_cutMarkSymbolSize", ["974"], null), // Cut Mark Symbol Size
        new(-1006620, StorageType.Integer, "974:m_cutLineType", ["974"], null), // Cut Line Type
        new(-1006619, StorageType.Double, "974:m_rotationAngle", ["974"], null), // Cut Line Angle
        new(-1006618, StorageType.Double, "974:m_extensionDistanceToBoundary", ["974"], null), // Cut Line Extension
        new(-1006616, StorageType.Integer, "974:m_cutMarkSymbol", ["974"], null), // Cut Mark Symbol
        new(-1006525, StorageType.String, "174:m_prefix", ["174/-2000150", "174/None"], null), // Dimension Prefix
        new(-1006523, StorageType.Integer, "174:m_interiorTickMarkDisplay", ["174/-2000150", "174/None"], null), // Interior Tick Mark Display
        new(-1006519, StorageType.String, "174:m_alternateUnitsSuffix", ["174/-2000150", "174/None"], null), // Alternate Units Suffix
        new(-1006518, StorageType.String, "174:m_alternateUnitsPrefix", ["174/-2000150", "174/None"], null), // Alternate Units Prefix
        new(-1006517, StorageType.Integer, "174:m_equalityWitnessDisplay", ["174/-2000150", "174/None"], null), // Equality Witness Display
        new(-1006516, StorageType.Integer, "174:m_suppressSpaces", ["174/-2000150", "174/None"], null), // Suppress Spaces
        new(-1006512, StorageType.String, "174:m_equalityText", ["174/-2000150", "174/None"], null), // Equality Text
        new(-1006511, StorageType.Integer, "174:m_leaderTextLocation", ["174/None"], null), // Text Location
        new(-1006510, StorageType.Integer, "174:m_leaderDisplayCondition", ["174/-2000150", "174/None"], null), // Show Leader When Text Moves
        new(-1006509, StorageType.Double, "174:m_leaderShoulderLength", ["174/None"], null), // Shoulder Length
        new(-1006508, StorageType.Integer, "174:m_leaderType", ["174/-2000150", "174/None"], null), // Leader Type
        new(-1006502, StorageType.Integer, "3475:m_bLeader", ["3475"], null), // Leader Line
        new(-1006477, StorageType.Double, "161:m_offset@0", ["2335/-2000261"], null), // Baseline Offset
        new(-1006469, StorageType.Integer, "3657:m_displayElevation", ["3657/-2000263"], null), // Display Elevations
        new(-1006467, StorageType.Integer, "174:m_linearDimType", ["174/-2000150", "174/None"], null), // Dimension String Type
        new(-1006466, StorageType.Integer, "3657:m_leader", ["3657/-2000263"], null), // Leader
        new(-1006464, StorageType.ElementId, "174:m_interiorTickMarkStyleId", ["174/-2000150", "174/None"], null), // Interior Tick Mark
        new(-1006456, StorageType.Integer, "174:m_alternateUnits", ["174/-2000150", "174/None"], null), // Alternate Units
        new(-1006449, StorageType.Integer, "2295:m_arrowClosed", ["2295"], null), // Arrow Closed
        new(-1006446, StorageType.Double, "174:m_dimLineSnapDist", ["174/-2000150", "174/None"], null), // Dimension Line Snap Distance
        new(-1006445, StorageType.ElementId, "174:m_centerlineTickMarkStyleId", ["174/-2000150", "174/None"], null), // Centerline Tick Mark
        new(-1006425, StorageType.Integer, "2295:m_arrowFilled", ["2295"], null), // Fill Tick
        new(-1006413, StorageType.Integer, "2295:m_tickType", ["2295"], null), // Arrow Style
        new(-1006406, StorageType.Integer, "174:m_arcCenterMark", ["174/None"], null), // Center Marks
        new(-1006325, StorageType.Integer, "3971:m_bArcLeaders", ["3971"], null), // Arc Leaders
        new(-1006323, StorageType.ElementId, "174:m_arrowHeadStyleId", ["174/-2000150", "174/None"], null), // Tick Mark
        new(-1006322, StorageType.String, "995:m_issueDate", ["995"], null), // Sheet Issue Date
        new(-1006313, StorageType.Integer, "625:m_bUnderline", ["697"], null), // Underline
        new(-1006312, StorageType.Integer, "3966:m_bItalic", ["3966"], null), // Italic
        new(-1006312, StorageType.Integer, "625:m_bItalic", ["697"], null), // Italic
        new(-1006311, StorageType.Integer, "3966:m_bBold", ["3966"], null), // Bold
        new(-1006311, StorageType.Integer, "625:m_bBold", ["697"], null), // Bold
        new(-1006015, StorageType.Double, "1918:m_steelCantilever", ["838/-2000045"], null), // Steel Cantilever
        new(-1006014, StorageType.Double, "1918:m_concreteCantilever", ["838/-2000045"], null), // Concrete Cantilever
        new(-1006006, StorageType.Integer, "1918:m_definesHeight", ["838/-2000045"], null), // Defines Constant Height
        new(-1005556, StorageType.String, "1773:m_structuralCodeName", ["1772/-2000035", "1772/-2000700", "1772/-2002000"], null), // Code Name
        new(-1005501, StorageType.Integer, "1862:m_backgroundPatternElemId", ["1862/-2002000"], null), // Section Shape
        new(-1005436, StorageType.Integer, "303:m_roughness", ["646"], null), // Roughness
        new(-1005334, StorageType.Integer, "3878:m_sunPathSize", ["3878"], null), // Sun path size (%)
        new(-1005252, StorageType.Integer, "4124:m_showExtensionLine", ["4124"], null), // Show Extension Line
        new(-1005251, StorageType.Integer, "4124:m_showLabel", ["4124"], null), // Show Title
        new(-1005250, StorageType.ElementId, "4124:m_familyTagId", ["4124"], null), // Title
        new(-1005201, StorageType.String, "1990:m_detailNumber", ["1990/-2000510"], null), // Detail Number
        new(-1005199, StorageType.ElementId, "108:m_viewTemplateId", ["1016", "986", "993"], null), // View Template
        new(-1005197, StorageType.Integer, "1773:m_eSymbolicRepType", ["1772/-2000700", "1772/-2002000"], null), // Symbolic Representation
        new(-1005196, StorageType.Integer, "1773:m_eRenderModelType", ["1772/-2000576", "1772/-2000700"], null), // Render Appearance Source
        new(-1005193, StorageType.Double, "1829:m_cutPlaneHeights@0", ["1829/-2001370"], null), // Height
        new(-1005177, StorageType.Integer, "1005:m_underlayOrientation", ["1005"], null), // Underlay Orientation
        new(-1005176, StorageType.ElementId, "108:m_viewTemplateId", ["1005/-2000279", "1016", "986", "993"], null), // View Template
        new(-1005161, StorageType.Integer, "108:m_modelDisplayMode", ["1005", "1016"], null), // Display Model
        new(-1005147, StorageType.ElementId, "108:m_fixedSketchPlaneId", ["1004", "1005/-2000279", "1011", "1016/-2000279", "993", "995"], null), // None
        new(-1005122, StorageType.String, "1020:m_refLabelStr", ["1020/-2000400", "1020/-2000538", "1020/-2006045", "1020/None", "1021"], null), // Reference Label
        new(-1005114, StorageType.String, "108:m_viewDescription", ["1004", "1005/-2000279", "1010", "1011", "1016/-2000279", "3145", "986/-2000279", "993/-2000279"], null), // Title on Sheet
        new(-1005112, StorageType.String, "108:m_viewName", ["1004", "1005/-2000279", "1010", "1011", "1016/-2000279", "3145", "986/-2000279", "993/-2000279"], null), // View Name
        new(-1005094, StorageType.Integer, "621:m_bAnnotationCropActive", ["4122/-2000500"], null), // Annotation Crop
        new(-1002559, StorageType.Integer, "977:m_isTop", ["1794/-2001330"], null), // Top is Attached
        new(-1002555, StorageType.Integer, "699:m_justification", ["1794/-2001330"], null), // Attachment Justification At Top
        new(-1002554, StorageType.Integer, "2479:m_shininess", ["2486", "29"], null), // Shininess
        new(-1002552, StorageType.Integer, "2479:m_glow", ["2486", "29"], null), // Glow
        new(-1002550, StorageType.Integer, "2479:m_color", ["2486", "29"], null), // Color
        new(-1002502, StorageType.String, "1773:m_omniClassCode", ["1772/-2000011", "1772/-2000035", "1772/-2000045", "1772/-2000150", "1772/-2000151", "1772/-2000196", "1772/-2000510", "1772/-2000576", "1772/-2000700", "1772/-2001300", "1772/-2002000", "1772/-2003400", "1772/-2003404", "1772/-2008143", "1772/-2009000", "1772/-2009030", "1772/None"], null), // OmniClass Number
        new(-1002501, StorageType.String, "3484:m_description", ["110", "3227"], null), // Assembly Description
        new(-1002501, StorageType.String, "3485:m_description", ["110", "3227"], null), // Assembly Description
        new(-1002401, StorageType.Double, "3626:m_endHeight", ["838"], null), // Height Offset at Head
        new(-1002108, StorageType.ElementId, "1795:m_hostId", ["1794/-2000014", "1794/-2000023", "1794/-2000170", "1794/-2000171", "1794/-2001320"], null), // Host Id
        new(-1002108, StorageType.ElementId, "2518:m_hostId", ["1794"], null), // Host Id
        new(-1002108, StorageType.ElementId, "897:m_hostId", ["1794"], null), // Host Id
        new(-1002107, StorageType.ElementId, "106:m_renderStyleId", ["1261", "1837", "3910", "3912", "646", "873"], null), // Material
        new(-1002107, StorageType.ElementId, "1534:m_sheetMaterial", ["1534"], null), // Material
        new(-1002107, StorageType.ElementId, "2027:m_materialId", ["3189", "3229"], null), // Material
        new(-1002066, StorageType.Double, "1801#1:m_offset", ["1794/-2000100", "1794/-2001330"], null), // Top Offset
        new(-1002066, StorageType.Double, "1801:m_offset", ["1794/-2000100", "1794/-2001330"], null), // Top Offset
        new(-1002065, StorageType.Double, "1801#0:m_offset", ["1794/-2000100", "1794/-2001330"], null), // Base Offset
        new(-1002064, StorageType.ElementId, "1781:m_elemId", ["1794/-2000100", "1794/-2001330"], null), // Top Level
        new(-1002064, StorageType.ElementId, "1801#1:m_elemId", ["1794/-2000100", "1794/-2001330"], null), // Top Level
        new(-1002063, StorageType.ElementId, "1801#0:m_elemId", ["1794/-2000100", "1794/-2001330"], null), // Base Level
        new(-1002063, StorageType.ElementId, "37:m_assocLevelId", ["1794/-2000100", "1794/-2001330"], null), // Base Level
        new(-1002062, StorageType.ElementId, "1794:m_scheduleOnlyLevelId", ["1794"], null), // Level
        new(-1002062, StorageType.ElementId, "37:m_assocLevelId", ["1794", "1913", "565", "642"], null), // Level
        new(-1002002, StorageType.String, "3905:m_name", ["1829/-2000948", "1829/-2005130", "1829/-2005301", "1829/-2006080", "1829/-2008074", "1829/-2008165", "1829/-2009005", "1829/-2009010", "1829/-2009011", "1829/-2009013", "1829/-2009021", "2071", "3122"], null), // Family Name
        new(-1002001, StorageType.String, "3905:m_name", ["1020", "1021", "1064", "110", "114", "1261", "1514", "1534", "1537", "1829", "1837", "1915", "2134", "2143", "2192/-2000560", "2202", "2266", "2323", "2364", "2406", "2471", "2497", "2528", "2540", "255", "2559", "2941", "3013", "3069", "3114", "3130", "3135", "3146", "3157", "3159", "3181", "3183", "3189", "3203", "3217", "3218", "3219", "3227", "3229", "3249", "3255", "3263", "3271", "3272", "3281", "3306", "3409", "3429", "3440", "3442", "3452", "3460", "3623", "3669", "3697", "3703", "3705", "3718", "3736", "3742", "3780", "383", "3910", "3912", "393", "3966", "3990", "4124", "512", "560", "571", "627", "646", "697", "762", "801", "803", "813", "873", "880", "908", "974"], null), // Type Name
        new(-1001952, StorageType.ElementId, "37:m_assocLevelId", ["1913", "565", "642"], null), // Level
        new(-1001821, StorageType.Integer, "1773:m_eProfileFamUsage", ["1772/-2000700", "1772/None"], null), // Profile Usage
        new(-1001708, StorageType.ElementId, "37:m_assocLevelId", ["3054"], null), // Base Level
        new(-1001707, StorageType.Double, "918:m_offset", ["838/-2000045"], null), // Overhang
        new(-1001706, StorageType.Double, "918:m_offset", ["838/-2000045"], null), // Wall offset
        new(-1001658, StorageType.Double, "1913:m_bottom", ["1913/-2000032"], null), // Elevation at Bottom Survey
        new(-1001657, StorageType.Double, "1913:m_top", ["1913/-2000032"], null), // Elevation at Top Survey
        new(-1001596, StorageType.Integer, "362:m_isStructuralSignificant", ["3524/-2000011"], null), // Structural
        new(-1001576, StorageType.Integer, "285:m_analyzesAs", ["285"], null), // Analyze As
        new(-1001575, StorageType.Integer, "1773:m_eBeamCutbackForColumn", ["1772/-2000700", "1772/-2002000"], null), // Beam cutback in plan
        new(-1001573, StorageType.Integer, "1708:m_orientation", ["1794/-2001320"], null), // Orientation
        new(-1001573, StorageType.Integer, "1794:m_insertOrientation", ["1794/-2001320"], null), // Orientation
        new(-1001560, StorageType.Integer, "1773:m_bStructUseCap", ["1772/-2000576", "1772/-2000700", "1772/-2001300", "1772/None"], null), // Cap
        new(-1001552, StorageType.Integer, "308:m_enableAnalyticalModel", ["1794", "3524", "804"], null), // Enable Analytical Model
        new(-1001550, StorageType.Integer, "1773:m_eStructMaterialType", ["1772/-2000035", "1772/-2000576", "1772/-2000700", "1772/-2001300", "1772/-2002000", "1772/None"], null), // Material for Model Behavior
        new(-1001503, StorageType.Integer, "3789:m_stickSymbolLocation", ["1794/-2001320"], null), // Stick Symbol Location
        new(-1001383, StorageType.ElementId, "1708:m_refLevelId", ["1794"], null), // Reference Level
        new(-1001381, StorageType.Integer, "1794:m_structUsage", ["1794/-2001320"], null), // Structural Usage
        new(-1001365, StorageType.ElementId, "1794:m_scheduleOnlyLevelId", ["1794/-2000151", "1794/-2001040", "1794/-2001060", "1794/-2001120", "1794/-2001140", "1794/-2001160", "1794/-2001300", "1794/-2008013", "1794/-2008087", "1794/-2008099"], null), // Schedule Level
        new(-1001361, StorageType.Double, "1795:m_elevation", ["1794/-2000014", "1794/-2000023"], null), // Sill Height
        new(-1001360, StorageType.Double, "1795:m_elevation", ["1794/-2000011", "1794/-2000127", "1794/-2003400"], null), // Elevation from Level
        new(-1001360, StorageType.Double, "2264:m_elevation", ["2264/-2001327"], null), // Elevation from Level
        new(-1001358, StorageType.Double, "1801#1:m_offset", ["1794"], null), // Top Offset
        new(-1001358, StorageType.Double, "1801:m_offset", ["1794"], null), // Top Offset
        new(-1001357, StorageType.Double, "1801#0:m_offset", ["1794"], null), // Base Offset
        new(-1001352, StorageType.ElementId, "1794:m_scheduleOnlyLevelId", ["1794/-2008013", "1794/-2008055"], null), // Level
        new(-1001352, StorageType.ElementId, "37:m_assocLevelId", ["1794/-2000014", "1794/-2000023", "1794/-2000032", "1794/-2000080", "1794/-2000151", "1794/-2001000", "1794/-2001040", "1794/-2001120", "1794/-2001140", "1794/-2001160", "1794/-2001180", "1794/-2001260", "1794/-2001300", "1794/-2001350", "1794/-2001360", "1794/-2001370", "1794/-2008010", "1794/-2008013", "1794/-2008016", "1794/-2008049", "1794/-2008055", "1794/-2008099", "1794/-2008126", "1794/-2008128", "1794/-2009030"], null), // Level
        new(-1001351, StorageType.ElementId, "1781:m_elemId", ["1794/-2000100", "1794/-2001000", "1794/-2001330"], null), // Top Level
        new(-1001351, StorageType.ElementId, "1801#1:m_elemId", ["1794"], null), // Top Level
        new(-1001350, StorageType.ElementId, "1801#0:m_elemId", ["1794"], null), // Base Level
        new(-1001350, StorageType.ElementId, "37:m_assocLevelId", ["1794/-2000100", "1794/-2001000", "1794/-2001330"], null), // Base Level
        new(-1001203, StorageType.String, "1710:m_doorNumber", ["1794"], null), // Mark
        new(-1001140, StorageType.ElementId, "1024:m_dPartOriginalCategoryId", ["1024"], null), // Original Category
        new(-1001138, StorageType.Integer, "1024:m_phaseDemolishedByOriginal", ["1024"], null), // Phase Demolished By Original
        new(-1001137, StorageType.Integer, "1024:m_phaseCreatedByOriginal", ["1024"], null), // Phase Created By Original
        new(-1001132, StorageType.String, "1024:m_dPartOriginalTypeName", ["1024"], null), // Original Type
        new(-1001131, StorageType.Integer, "2598:m_showShapeHandles", ["1024"], null), // Show Shape Handles
        new(-1001128, StorageType.Integer, "1024:m_materialByOriginal", ["1024"], null), // Material By Original
        new(-1001127, StorageType.ElementId, "1024:m_dPartMaterial", ["1024"], null), // Material
        new(-1001126, StorageType.String, "1024:m_dPartOriginalFamilyName", ["1024"], null), // Original Family
        new(-1001122, StorageType.Integer, "362:m_wallKeyRef", ["3524/-2000011"], null), // Location Line
        new(-1001117, StorageType.Integer, "2262:m_baseOrTop", ["3524/-2000011"], null), // Top is Attached
        new(-1001107, StorageType.ElementId, "37:m_assocLevelId", ["3524", "3525", "3568"], null), // Base Constraint
        new(-1001103, StorageType.ElementId, "362:m_upToLevelId", ["3524"], null), // Top Constraint
        new(-1001007, StorageType.Integer, "1794:m_roomBounding", ["1794/-2000011", "1794/-2000032", "1794/-2000100", "1794/-2001330"], null), // Room Bounding
        new(-1001007, StorageType.Integer, "363:m_roomBounding", ["1604", "1913/-2000032", "3054", "3451", "3524/-2000011", "565", "642"], null), // Room Bounding
        new(-1001006, StorageType.Integer, "513:m_function", ["2559", "512"], null),
    ];
    internal static readonly HashSet<(int ParamId, string Key)> NotWithCompleteStores = [(-1140422, "2960:m_grade"), (-1010108, "2960:m_grade"), (-1010104, "2960:m_grade")];

    internal static readonly HashSet<string> Keys = All.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);

    internal static readonly HashSet<int> DeclaringClasses =
        All.Select(entry => int.Parse(entry.Key[..entry.Key.IndexOfAny(['#', ':'])], System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
}
