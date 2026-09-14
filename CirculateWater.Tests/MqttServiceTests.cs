using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using System.Net;
using System.Text.Json;

namespace CirculateWater.Tests;

/// <summary>
/// Runs <see cref="MqttService"/> against an in-process broker, observed by a client acting as the dashboard.
/// </summary>
public class MqttServiceTests : IAsyncLifetime
{
    private const string Prefix = "test/circulate";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly string directory = Directory.CreateTempSubdirectory("circulate-mqtt-").FullName;
    private readonly StatusTracker status = new(TimeProvider.System);
    private int port;
    private string appSettingsPath;
    private string overridesPath;
    private MqttServer server;
    private IConfigurationRoot config;
    private MqttService service;
    private RecordingClient dashboard;

    public async ValueTask InitializeAsync()
    {
        var serverFactory = new MqttServerFactory();
        var serverOptions = serverFactory.CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointBoundIPV6Address(IPAddress.None)
            .WithDefaultEndpointPort(0)
            .Build();
        server = serverFactory.CreateMqttServer(serverOptions);
        await server.StartAsync();
        // The listener records the port the OS picked
        port = serverOptions.DefaultEndpointOptions.Port;

        appSettingsPath = Path.Combine(directory, "appsettings.json");
        overridesPath = Path.Combine(directory, ConfigEditor.OverridesFileName);
        File.WriteAllText(appSettingsPath, $$"""
            {
              "CirculateWater": {
                "Stage1": { "TempThresholdF": 36, "CirculateFrequencyMins": 20, "CirculateDurationSecs": 10 },
                "CerboIP": "192.168.10.100",
                "SensorVRMInstance": 34,
                "TempCheckFrequencySecs": 60
              },
              "Mqtt": { "Host": "127.0.0.1", "Port": {{port}}, "TopicPrefix": "{{Prefix}}" }
            }
            """);
        config = AppConfiguration.Build(directory, status, (_, _) => { }, out _);
        service = new MqttService(config, NullLoggerFactory.Instance, status, CreateEditor());
        dashboard = await RecordingClient.ConnectAsync(port, "dashboard");
    }

    public async ValueTask DisposeAsync()
    {
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();
        dashboard.Dispose();
        server.Dispose();
        (config as IDisposable)?.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task PublishesAvailabilityConfigAndStatus_WhenConnected()
    {
        status.RecordReading(21.5, 1);

        await StartServiceAsync();

        Assert.Equal("online", (await dashboard.WaitForAsync(Topic("availability"))).Payload);
        using var configJson = JsonDocument.Parse((await dashboard.WaitForAsync(Topic("config"))).Payload);
        Assert.Equal(36, configJson.RootElement.GetProperty("Stage1").GetProperty("TempThresholdF").GetInt32());
        using var statusJson = JsonDocument.Parse((await dashboard.WaitForAsync(Topic("status"))).Payload);
        Assert.Equal(21.5, statusJson.RootElement.GetProperty("currentTemperatureF").GetDouble());
        Assert.Equal(1, statusJson.RootElement.GetProperty("activeStage").GetInt32());
        Assert.False(statusJson.RootElement.GetProperty("solenoidIsOpen").GetBoolean());
    }

    [Fact]
    public async Task PublishesSettingsInEffect_IncludingOverrides()
    {
        File.WriteAllText(overridesPath, """{ "CirculateWater": { "Stage1": { "TempThresholdF": 32 } } }""");

        await StartServiceAsync();

        using var configJson = JsonDocument.Parse((await dashboard.WaitForAsync(Topic("config"))).Payload);
        var stage1 = configJson.RootElement.GetProperty("Stage1");
        Assert.Equal(32, stage1.GetProperty("TempThresholdF").GetInt32());
        Assert.Equal(20, stage1.GetProperty("CirculateFrequencyMins").GetInt32());
    }

    [Fact]
    public async Task PublishesStatus_WhenItChanges()
    {
        await StartServiceAsync();
        await dashboard.WaitForAsync(Topic("availability"), m => m.Payload == "online");

        status.RecordCirculationStarted(10);

        await dashboard.WaitForAsync(Topic("status"), m => m.Payload.Contains("\"solenoidIsOpen\":true"));
    }

    [Fact]
    public async Task RetainsMessages_ForLateSubscribers()
    {
        await StartServiceAsync();
        await dashboard.WaitForAsync(Topic("config"));
        await dashboard.WaitForAsync(Topic("status"));

        using var late = await RecordingClient.ConnectAsync(port, "late");

        Assert.True((await late.WaitForAsync(Topic("availability"))).Retain);
        Assert.True((await late.WaitForAsync(Topic("config"))).Retain);
        Assert.True((await late.WaitForAsync(Topic("status"))).Retain);
    }

    [Fact]
    public async Task AppliesConfigEdit_AndPublishesResultAndUpdatedConfig()
    {
        await StartServiceAsync();
        await dashboard.WaitForAsync(Topic("availability"), m => m.Payload == "online");

        await dashboard.PublishAsync(Topic("config/set"), """{ "Stage1": { "TempThresholdF": 30 } }""");

        using var result = JsonDocument.Parse((await dashboard.WaitForAsync(Topic("config/set/result"))).Payload);
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        await dashboard.WaitForAsync(Topic("config"), m => m.Payload.Contains("\"TempThresholdF\":30"));
        Assert.Equal(30, CreateEditor().ReadSection()["Stage1"]["TempThresholdF"].GetValue<int>());
        Assert.True(File.Exists(overridesPath));
    }

    [Fact]
    public async Task RejectsInvalidEdit_WithoutChangingConfig()
    {
        await StartServiceAsync();
        await dashboard.WaitForAsync(Topic("availability"), m => m.Payload == "online");

        await dashboard.PublishAsync(Topic("config/set"), """{ "Stage1": { "CirculateDurationSecs": 0 } }""");

        using var result = JsonDocument.Parse((await dashboard.WaitForAsync(Topic("config/set/result"))).Payload);
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.NotEqual(0, result.RootElement.GetProperty("errors").GetArrayLength());
        Assert.Equal(10, CreateEditor().ReadSection()["Stage1"]["CirculateDurationSecs"].GetValue<int>());
    }

    [Fact]
    public async Task IgnoresRetainedEdits_WhenConnecting()
    {
        await dashboard.PublishAsync(Topic("config/set"), """{ "Stage1": { "TempThresholdF": 30 } }""", retain: true);

        await StartServiceAsync();

        using var result = JsonDocument.Parse((await dashboard.WaitForAsync(Topic("config/set/result"))).Payload);
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(36, CreateEditor().ReadSection()["Stage1"]["TempThresholdF"].GetValue<int>());
    }

    [Fact]
    public async Task RejectsRetainedEdits_WhileConnected()
    {
        await StartServiceAsync();
        await dashboard.WaitForAsync(Topic("availability"), m => m.Payload == "online");

        await dashboard.PublishAsync(Topic("config/set"), """{ "Stage1": { "TempThresholdF": 30 } }""", retain: true);

        using var result = JsonDocument.Parse((await dashboard.WaitForAsync(Topic("config/set/result"))).Payload);
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(36, CreateEditor().ReadSection()["Stage1"]["TempThresholdF"].GetValue<int>());
    }

    [Fact]
    public async Task IgnoresEmptyEdits()
    {
        await StartServiceAsync();
        await dashboard.WaitForAsync(Topic("availability"), m => m.Payload == "online");

        // Clearing a retained edit sends an empty message, which shouldn't produce a failure result
        await dashboard.PublishAsync(Topic("config/set"), "", retain: true);
        await dashboard.PublishAsync(Topic("config/set"), """{ "Stage1": { "TempThresholdF": 30 } }""");

        using var result = JsonDocument.Parse((await dashboard.WaitForAsync(Topic("config/set/result"))).Payload);
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task PublishesOffline_WhenStopped()
    {
        await StartServiceAsync();
        await dashboard.WaitForAsync(Topic("availability"), m => m.Payload == "online");

        await service.StopAsync(TestContext.Current.CancellationToken);

        await dashboard.WaitForAsync(Topic("availability"), m => m.Payload == "offline");
    }

    [Fact]
    public async Task Stops_WhenHostNotConfigured()
    {
        var noHost = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["Mqtt:Host"] = "" })
            .Build();
        using var disabled = new MqttService(noHost, NullLoggerFactory.Instance, status, CreateEditor());

        await disabled.StartAsync(TestContext.Current.CancellationToken);

        await disabled.ExecuteTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        Assert.True(disabled.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DoesNotFault_WhenMqttSettingsInvalid()
    {
        var badPort = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["Mqtt:Host"] = "127.0.0.1", ["Mqtt:Port"] = "not a port" })
            .Build();
        using var misconfigured = new MqttService(badPort, NullLoggerFactory.Instance, status, CreateEditor());

        await misconfigured.StartAsync(TestContext.Current.CancellationToken);

        await misconfigured.ExecuteTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        Assert.True(misconfigured.ExecuteTask.IsCompletedSuccessfully);
    }

    private Task StartServiceAsync() => service.StartAsync(TestContext.Current.CancellationToken);

    private ConfigEditor CreateEditor() => new(appSettingsPath, overridesPath);

    private static string Topic(string name) => $"{Prefix}/{name}";

    private sealed record Received(string Topic, string Payload, bool Retain);

    /// <summary>
    /// MQTT client subscribed to every test topic that keeps what it receives.
    /// </summary>
    private sealed class RecordingClient : IDisposable
    {
        private readonly IMqttClient client = new MqttClientFactory().CreateMqttClient();
        private readonly List<Received> messages = [];
        private readonly SemaphoreSlim arrived = new(0);

        public static async Task<RecordingClient> ConnectAsync(int port, string clientId)
        {
            var recorder = new RecordingClient();
            recorder.client.ApplicationMessageReceivedAsync += e =>
            {
                // Copy the payload now; the buffer isn't guaranteed to outlive the handler
                var message = new Received(e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString(), e.ApplicationMessage.Retain);
                lock (recorder.messages)
                {
                    recorder.messages.Add(message);
                }
                recorder.arrived.Release();
                return Task.CompletedTask;
            };

            var options = new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).WithClientId(clientId).Build();
            await recorder.client.ConnectAsync(options, TestContext.Current.CancellationToken);
            await recorder.client.SubscribeAsync($"{Prefix}/#", MqttQualityOfServiceLevel.AtLeastOnce, TestContext.Current.CancellationToken);
            return recorder;
        }

        public Task PublishAsync(string topic, string payload, bool retain = false) =>
            client.PublishStringAsync(topic, payload, MqttQualityOfServiceLevel.AtLeastOnce, retain, TestContext.Current.CancellationToken);

        public async Task<Received> WaitForAsync(string topic, Func<Received, bool> matches = null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(WaitTimeout);
            while (true)
            {
                lock (messages)
                {
                    var found = messages.FirstOrDefault(m => m.Topic == topic && (matches == null || matches(m)));
                    if (found != null)
                    {
                        return found;
                    }
                }

                await arrived.WaitAsync(timeout.Token);
            }
        }

        public void Dispose()
        {
            client.Dispose();
            arrived.Dispose();
        }
    }
}
