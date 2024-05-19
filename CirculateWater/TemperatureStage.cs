using Microsoft.Extensions.Configuration;

namespace CirculateWater;

/// <summary>
/// Represents a temperature range with specific settings.
/// </summary>
internal class TemperatureStage
{
    public const string STAGE_PREFIX = "CirculateWater:Stage";
    public int StageNumber { get; private set; }
    public int TempThresholdF { get; private set; }
    public int CirculateFrequencyMins { get; private set; }
    public int CirculateDurationSecs { get; private set; }

    public TemperatureStage(int stageNumber, IConfiguration config)
    {
        StageNumber = stageNumber;
        TempThresholdF = int.Parse(config[$"{STAGE_PREFIX}{stageNumber}:{nameof(TempThresholdF)}"]);
        CirculateFrequencyMins = int.Parse(config[$"{STAGE_PREFIX}{stageNumber}:{nameof(CirculateFrequencyMins)}"]);
        CirculateDurationSecs = int.Parse(config[$"{STAGE_PREFIX}{stageNumber}:{nameof(CirculateDurationSecs)}"]);
    }
}
