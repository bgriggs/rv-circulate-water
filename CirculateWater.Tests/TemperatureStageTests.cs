using Microsoft.Extensions.Configuration;

namespace CirculateWater.Tests;

public class TemperatureStageTests
{
    [Fact]
    public void ReadsSettingsForStageNumber()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["CirculateWater:Stage1:TempThresholdF"] = "36",
                ["CirculateWater:Stage1:CirculateFrequencyMins"] = "20",
                ["CirculateWater:Stage1:CirculateDurationSecs"] = "10",
                ["CirculateWater:Stage2:TempThresholdF"] = "18",
                ["CirculateWater:Stage2:CirculateFrequencyMins"] = "10",
                ["CirculateWater:Stage2:CirculateDurationSecs"] = "15",
            })
            .Build();

        var stage = new TemperatureStage(2, config);

        Assert.Equal(2, stage.StageNumber);
        Assert.Equal(18, stage.TempThresholdF);
        Assert.Equal(10, stage.CirculateFrequencyMins);
        Assert.Equal(15, stage.CirculateDurationSecs);
    }

    [Fact]
    public void Throws_WhenSettingMissing()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["CirculateWater:Stage1:TempThresholdF"] = "36",
            })
            .Build();

        Assert.Throws<ArgumentNullException>(() => new TemperatureStage(1, config));
    }
}
