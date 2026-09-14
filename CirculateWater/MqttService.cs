using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using System.Text.Json;
using System.Threading.Channels;

namespace CirculateWater;

/// <summary>
/// Broker settings from the Mqtt section of appsettings.json. Changes take effect after a restart.
/// </summary>
internal sealed class MqttSettings
{
    public string Host { get; set; }
    public int Port { get; set; } = 1883;
    public string Username { get; set; }
    public string Password { get; set; }
    public string TopicPrefix { get; set; } = "rv/circulate";
}

/// <summary>
/// Topic names under the configured prefix.
/// </summary>
internal sealed class MqttTopics(string prefix)
{
    private readonly string root = prefix.TrimEnd('/');

    public string Availability => $"{root}/availability";
    public string Status => $"{root}/status";
    public string Config => $"{root}/config";
    public string ConfigSet => $"{root}/config/set";
    public string ConfigSetResult => $"{root}/config/set/result";
}

/// <summary>
/// Publishes status and configuration to MQTT for monitoring, and applies configuration edits received on config/set.
/// MQTT problems are logged and retried; they never affect circulation.
/// </summary>
internal sealed class MqttService : BackgroundService
{
    private const string Online = "online";
    private const string Offline = "offline";
    private const string RetainedEditError = "Retained edits are rejected; publish to config/set without the retain flag";
    private const uint MaxPacketSize = 64 * 1024;

    private static readonly TimeSpan RepublishInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly StatusTracker status;
    private readonly ConfigEditor configEditor;

    // Wakes the publish loop when status or config changes; one pending wake-up covers any number of changes
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private int configChanged = 1;

    private IConfiguration Config { get; }
    private ILogger Logger { get; }

    public MqttService(IConfiguration config, ILoggerFactory loggerFactory, StatusTracker status, ConfigEditor configEditor)
    {
        Config = config;
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.status = status;
        this.configEditor = configEditor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            // MQTT is only for monitoring, so it must never stop the host, and circulation with it
            if (!stoppingToken.IsCancellationRequested)
            {
                Logger.LogError(ex, "MQTT stopped after an unexpected error");
            }
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var settings = Config.GetSection("Mqtt").Get<MqttSettings>() ?? new MqttSettings();
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            Logger.LogInformation("MQTT disabled: Mqtt:Host is not set");
            return;
        }

        var topics = new MqttTopics(settings.TopicPrefix);
        var factory = new MqttClientFactory();
        var options = BuildOptions(settings, topics);
        // Retain-as-published marks retained edits even when they arrive while connected, so they can always be rejected
        var subscribeOptions = factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(topics.ConfigSet, MqttQualityOfServiceLevel.AtLeastOnce, retainAsPublished: true)
            .Build();

        using var client = factory.CreateMqttClient();
        client.ApplicationMessageReceivedAsync += e => OnMessageReceivedAsync(client, topics, e);
        using var configReload = ChangeToken.OnChange(Config.GetReloadToken, OnConfigChanged);
        status.Changed += Wake;
        try
        {
            var failing = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                // Not linked to stoppingToken, so a publish in flight at shutdown finishes instead of stalling the offline message
                using (var attempt = new CancellationTokenSource(OperationTimeout))
                {
                    try
                    {
                        if (!client.IsConnected)
                        {
                            await ConnectAsync(client, options, subscribeOptions, topics, attempt.Token);
                            Interlocked.Exchange(ref configChanged, 1);
                            Logger.LogInformation("Connected to MQTT broker {Host}:{Port}", settings.Host, settings.Port);
                            failing = false;
                        }

                        await PublishConfigIfChangedAsync(client, topics, attempt.Token);
                        await PublishRetainedAsync(client, topics.Status, JsonSerializer.Serialize(status.GetSnapshot(), JsonOptions), attempt.Token);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        if (!failing)
                        {
                            Logger.LogWarning(ex, "MQTT broker {Host}:{Port} unavailable, retrying every {Seconds}s", settings.Host, settings.Port, ReconnectInterval.TotalSeconds);
                            failing = true;
                        }

                        // Start over with a fresh connection and subscription
                        await DisconnectQuietlyAsync(client);
                    }
                }

                await WaitForWakeAsync(client.IsConnected ? RepublishInterval : ReconnectInterval, stoppingToken);
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping; MQTTnet can report an interrupted operation as a timeout rather than a cancellation
        }
        finally
        {
            status.Changed -= Wake;
            await PublishOfflineAsync(client, topics);
        }
    }

    private static async Task ConnectAsync(IMqttClient client, MqttClientOptions options, MqttClientSubscribeOptions subscribeOptions, MqttTopics topics, CancellationToken cancellationToken)
    {
        var result = await client.ConnectAsync(options, cancellationToken);
        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new InvalidOperationException($"Broker refused the connection: {result.ResultCode} {result.ReasonString}");
        }

        await client.SubscribeAsync(subscribeOptions, cancellationToken);
        await PublishRetainedAsync(client, topics.Availability, Online, cancellationToken);
    }

    private async Task PublishConfigIfChangedAsync(IMqttClient client, MqttTopics topics, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref configChanged, 0) == 0)
        {
            return;
        }

        string section;
        try
        {
            section = configEditor.ReadSection().ToJsonString();
        }
        catch (Exception ex)
        {
            // A settings file problem isn't a broker problem, so report it without reconnecting
            Logger.LogError(ex, "Failed to read settings to publish over MQTT");
            return;
        }

        try
        {
            await PublishRetainedAsync(client, topics.Config, section, cancellationToken);
        }
        catch
        {
            Interlocked.Exchange(ref configChanged, 1);
            throw;
        }
    }

    private async Task OnMessageReceivedAsync(IMqttClient client, MqttTopics topics, MqttApplicationMessageReceivedEventArgs e)
    {
        if (e.ApplicationMessage.Topic != topics.ConfigSet)
        {
            return;
        }

        var edit = e.ApplicationMessage.ConvertPayloadToString();
        if (string.IsNullOrEmpty(edit))
        {
            // Empty messages clear a retained edit; there's nothing to apply
            return;
        }

        ConfigEditResult result;
        try
        {
            // A retained edit would be re-applied on every reconnect, undoing later changes
            result = e.ApplicationMessage.Retain
                ? new ConfigEditResult(false, [RetainedEditError], configEditor.ReadSection())
                : configEditor.Apply(edit);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to apply config edit from MQTT");
            result = new ConfigEditResult(false, [$"Failed to apply edit: {ex.Message}"], null);
        }

        if (result.Success)
        {
            Logger.LogInformation("Config edited over MQTT: {Edit}", edit);
            OnConfigChanged();
        }
        else
        {
            Logger.LogWarning("Rejected config edit over MQTT: {Errors}", string.Join("; ", result.Errors));
        }

        try
        {
            // QoS 0: the result is informational, and waiting for an acknowledgement would hold up the next incoming message
            await client.PublishStringAsync(topics.ConfigSetResult, JsonSerializer.Serialize(result, JsonOptions), MqttQualityOfServiceLevel.AtMostOnce, retain: false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to publish config edit result");
        }
    }

    private void OnConfigChanged()
    {
        Interlocked.Exchange(ref configChanged, 1);
        Wake();
    }

    private void Wake() => wake.Writer.TryWrite(true);

    private async Task WaitForWakeAsync(TimeSpan timeout, CancellationToken stoppingToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await wake.Reader.ReadAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Timed out; publish again anyway
        }
    }

    /// <summary>
    /// A clean disconnect doesn't send the will message, so mark the service offline before disconnecting.
    /// </summary>
    private async Task PublishOfflineAsync(IMqttClient client, MqttTopics topics)
    {
        if (!client.IsConnected)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await PublishRetainedAsync(client, topics.Availability, Offline, timeout.Token);
            await client.DisconnectAsync(cancellationToken: timeout.Token);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to disconnect from MQTT broker cleanly");
        }
    }

    private static async Task DisconnectQuietlyAsync(IMqttClient client)
    {
        try
        {
            using var timeout = new CancellationTokenSource(OperationTimeout);
            await client.DisconnectAsync(cancellationToken: timeout.Token);
        }
        catch
        {
            // Already disconnected or unreachable; the next attempt reconnects from scratch
        }
    }

    private static Task PublishRetainedAsync(IMqttClient client, string topic, string payload, CancellationToken cancellationToken) =>
        client.PublishStringAsync(topic, payload, MqttQualityOfServiceLevel.AtLeastOnce, retain: true, cancellationToken);

    private static MqttClientOptions BuildOptions(MqttSettings settings, MqttTopics topics)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithClientId($"circulate-water-{Environment.MachineName}")
            .WithCleanSession()
            // MQTT 5 for retain-as-published and a maximum packet size, so oversized messages can't exhaust memory
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithMaximumPacketSize(MaxPacketSize)
            .WithWillTopic(topics.Availability)
            .WithWillPayload(Offline)
            .WithWillRetain()
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (!string.IsNullOrEmpty(settings.Username))
        {
            builder.WithCredentials(settings.Username, settings.Password);
        }

        return builder.Build();
    }
}
