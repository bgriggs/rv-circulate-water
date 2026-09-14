using Microsoft.Extensions.Configuration;

namespace CirculateWater.Tests;

public class AppConfigurationTests : IDisposable
{
    private const string AppSettings = """{ "CirculateWater": { "CerboIP": "192.168.10.100", "Stage1": { "TempThresholdF": 36 } } }""";

    private readonly string directory = Directory.CreateTempSubdirectory("circulate-appconfig-").FullName;
    private readonly StatusTracker status = new(TimeProvider.System);
    private readonly List<string> loggedErrors = [];

    public AppConfigurationTests()
    {
        File.WriteAllText(Path.Combine(directory, AppConfiguration.AppSettingsFileName), AppSettings);
    }

    private string OverridesPath => Path.Combine(directory, ConfigEditor.OverridesFileName);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void AppliesOverrides()
    {
        File.WriteAllText(OverridesPath, """{ "CirculateWater": { "CerboIP": "192.168.10.101" } }""");

        using var config = Build(out var overridesIgnored);

        Assert.False(overridesIgnored);
        Assert.Equal("192.168.10.101", config["CirculateWater:CerboIP"]);
        Assert.Equal("36", config["CirculateWater:Stage1:TempThresholdF"]);
        Assert.Null(status.GetSnapshot().LastError);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("""{ "CirculateWater": { "CerboIP": "10.0.0.7", "cerboip": "10.0.0.8" } }""")]
    public void StartsWithAppSettingsValues_WhenOverridesInvalid(string overrides)
    {
        File.WriteAllText(OverridesPath, overrides);

        using var config = Build(out var overridesIgnored);

        Assert.True(overridesIgnored);
        Assert.Equal("192.168.10.100", config["CirculateWater:CerboIP"]);
        Assert.StartsWith($"{ConfigEditor.OverridesFileName} is invalid and ignored: ", status.GetSnapshot().LastError);
        Assert.NotEmpty(loggedErrors);
    }

    [Fact]
    public async Task RevertsToAppSettingsValues_WhenOverridesBecomeInvalid()
    {
        File.WriteAllText(OverridesPath, """{ "CirculateWater": { "CerboIP": "192.168.10.101" } }""");
        using var config = Build(out _);
        Assert.Equal("192.168.10.101", config["CirculateWater:CerboIP"]);

        File.WriteAllText(OverridesPath, "{ not json");

        await WaitUntilAsync(() => config["CirculateWater:CerboIP"] == "192.168.10.100" && status.GetSnapshot().LastError != null);
        Assert.StartsWith($"{ConfigEditor.OverridesFileName} is invalid and ignored: ", status.GetSnapshot().LastError);
    }

    private ConfigurationRoot Build(out bool overridesIgnored) =>
        AppConfiguration.Build(directory, status, (_, message) =>
        {
            lock (loggedErrors)
            {
                loggedErrors.Add(message);
            }
        }, out overridesIgnored);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(50, timeout.Token);
        }
    }
}
