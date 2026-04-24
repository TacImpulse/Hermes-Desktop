namespace Hermes.Agent.Gateway;

using Hermes.Agent.Core;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

// ══════════════════════════════════════════════
// Gateway Service — multi-platform messaging hub
// ══════════════════════════════════════════════
//
// Upstream ref: gateway/run.py GatewayRunner
// Key patterns: adapter factory, session routing, stale agent detection,
// failed platform retry with exponential backoff, authorization tiers

/// <summary>
/// Manages the lifecycle of all platform adapters and routes messages
/// to/from the agent. Long-running background service.
/// </summary>
public sealed class GatewayService
{
    private readonly ILogger<GatewayService> _logger;
    private readonly GatewayConfig _config;
    private readonly ConcurrentDictionary<Platform, IPlatformAdapter> _adapters = new();
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    private readonly ConcurrentDictionary<string, DateTime> _activeAgents = new();
    private readonly ConcurrentDictionary<Platform, FailedPlatformInfo> _failedPlatforms = new();
    private readonly ConcurrentDictionary<Platform, string> _lastChatIds = new();

    private Func<string, string, string, Task<string>>? _agentHandler;
    private bool _running;

    public GatewayService(GatewayConfig config, ILogger<GatewayService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Connected adapters.</summary>
    public IReadOnlyDictionary<Platform, IPlatformAdapter> Adapters => _adapters;

    /// <summary>Whether the gateway is running.</summary>
    public bool IsRunning => _running;

    // ══════════════════════════════════════════
    // Agent Handler Registration
    // ══════════════════════════════════════════

    /// <summary>
    /// Register the handler that processes incoming messages via the agent.
    /// Signature: (sessionId, userMessage, platform) → agentResponse
    /// </summary>
    public void SetAgentHandler(Func<string, string, string, Task<string>> handler)
    {
        _agentHandler = handler;
    }

    // ══════════════════════════════════════════
    // Startup
    // ══════════════════════════════════════════

    /// <summary>
    /// Start the gateway — connect all enabled platforms.
    /// Failed connections are queued for background retry.
    /// </summary>
    public async Task StartAsync(IEnumerable<IPlatformAdapter> adapters, CancellationToken ct)
    {
        _running = true;
        _logger.LogInformation("Gateway starting with {Count} configured platforms", _config.Platforms.Count);

        foreach (var adapter in adapters)
        {
            var platformConfig = _config.Platforms.GetValueOrDefault(adapter.Platform);
            if (platformConfig is null || !platformConfig.Enabled)
            {
                _logger.LogDebug("Skipping disabled platform: {Platform}", adapter.Platform);
                continue;
            }

            adapter.SetMessageHandler(evt => HandleMessageAsync(evt, ct));
            adapter.SetErrorHandler(OnPlatformError);

            try
            {
                var success = await adapter.ConnectAsync(ct);
                if (success)
                {
                    _adapters[adapter.Platform] = adapter;
                    _logger.LogInformation("Connected to {Platform}", adapter.Platform);
                }
                else
                {
                    QueueForRetry(adapter);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to {Platform} — queued for retry", adapter.Platform);
                QueueForRetry(adapter);
            }
        }

        // Start background watchers
        _ = Task.Run(() => SessionExpiryWatcherAsync(ct), ct);
        _ = Task.Run(() => PlatformReconnectWatcherAsync(ct), ct);

        _logger.LogInformation("Gateway started: {Connected} connected, {Failed} queued for retry",
            _adapters.Count, _failedPlatforms.Count);
    }

    // ══════════════════════════════════════════
    // Message Handling Pipeline
    // ══════════════════════════════════════════

    private async Task<string?> HandleMessageAsync(MessageEvent evt, CancellationToken ct)
    {
        _lastChatIds[evt.Source.Platform] = evt.Source.ChatId;
        var sessionKey = BuildSessionKey(evt.Source);

        // ── Authorization ──
        if (!IsUserAuthorized(evt.Source))
        {
            _logger.LogWarning("Unauthorized message from {User} on {Platform}",
                evt.Source.UserId, evt.Source.Platform);
            return "You are not authorized to use this agent. Contact the administrator.";
        }

        // ── Stale agent detection ──
        if (_activeAgents.TryGetValue(sessionKey, out var startTime))
        {
            var age = DateTime.UtcNow - startTime;
            if (age.TotalSeconds > 600) // 10 min wall-clock stale threshold
            {
                _activeAgents.TryRemove(sessionKey, out _);
                _logger.LogWarning("Evicted stale agent for session {Key} (age: {Age}s)", sessionKey, age.TotalSeconds);
            }
            else
            {
                // Agent is already processing — queue or reject
                return "I'm still working on your previous request. Please wait or type /stop to cancel.";
            }
        }

        // ── Command dispatch ──
        var command = evt.GetCommand();
        if (command is not null)
        {
            return await HandleCommandAsync(command, evt);
        }

        // ── Route to agent ──
        if (_agentHandler is null)
        {
            return "Agent handler not configured.";
        }

        _activeAgents[sessionKey] = DateTime.UtcNow;
        try
        {
            var routedMessage = BuildAgentMessage(evt);
            var response = await _agentHandler(sessionKey, routedMessage, evt.Source.Platform.ToString());
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent failed for session {Key}", sessionKey);
            return $"An error occurred: {ex.Message}";
        }
        finally
        {
            _activeAgents.TryRemove(sessionKey, out _);
        }
    }

    // ══════════════════════════════════════════
    // Commands
    // ══════════════════════════════════════════

    private Task<string?> HandleCommandAsync(string command, MessageEvent evt)
    {
        return command switch
        {
            "new" or "reset" => HandleResetAsync(evt),
            "stop" => HandleStopAsync(evt),
            "status" => Task.FromResult<string?>($"Gateway: {_adapters.Count} platforms connected, {_sessions.Count} sessions active."),
            "help" => Task.FromResult<string?>(
                "Available commands:\n" +
                "/new — Start a new conversation\n" +
                "/stop — Cancel current generation\n" +
                "/status — Show gateway status\n" +
                "/help — Show this help"),
            _ => Task.FromResult<string?>($"Unknown command: /{command}. Type /help for available commands.")
        };
    }

    private Task<string?> HandleResetAsync(MessageEvent evt)
    {
        var sessionKey = BuildSessionKey(evt.Source);
        _sessions.TryRemove(sessionKey, out _);
        _activeAgents.TryRemove(sessionKey, out _);
        return Task.FromResult<string?>("Conversation reset. Send a new message to start fresh.");
    }

    private Task<string?> HandleStopAsync(MessageEvent evt)
    {
        var sessionKey = BuildSessionKey(evt.Source);
        _activeAgents.TryRemove(sessionKey, out _);
        return Task.FromResult<string?>("Generation stopped.");
    }

    // ══════════════════════════════════════════
    // Message Delivery (outbound)
    // ══════════════════════════════════════════

    /// <summary>
    /// Send a message to a specific platform and chat.
    /// Used by SendMessageTool and cron delivery.
    /// </summary>
    public async Task<DeliveryResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        if (!_adapters.TryGetValue(message.Platform, out var adapter))
            return DeliveryResult.Fail($"Platform {message.Platform} is not connected.");

        try
        {
            return await adapter.SendAsync(message, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to {Platform}/{Chat}",
                message.Platform, message.ChatId);
            return DeliveryResult.Fail(ex.Message);
        }
    }

    /// <summary>Send a text message (convenience wrapper).</summary>
    public Task<DeliveryResult> SendTextAsync(Platform platform, string chatId, string text, CancellationToken ct) =>
        SendAsync(new OutboundMessage { Platform = platform, ChatId = chatId, Text = text }, ct);

    /// <summary>
    /// Try to get the last inbound chat for a platform.
    /// Used for channel-native replies when the caller omits ChatId.
    /// </summary>
    public bool TryGetLastChatId(Platform platform, out string chatId) =>
        _lastChatIds.TryGetValue(platform, out chatId!);

    // ══════════════════════════════════════════
    // Authorization (upstream: 6-tier)
    // ══════════════════════════════════════════

    private bool IsUserAuthorized(SessionSource source)
    {
        // Tier 1: Platform exemptions (Webhook, API always allowed)
        if (source.Platform is Platform.Webhook or Platform.Api)
            return true;

        // Tier 2: Per-platform allow-all flag
        if (_config.Platforms.TryGetValue(source.Platform, out var pc) &&
            pc.Extra.TryGetValue("allow_all_users", out var allowAll) &&
            string.Equals(allowAll, "true", StringComparison.OrdinalIgnoreCase))
            return true;

        // Tier 3: Global allowlist
        if (!string.IsNullOrWhiteSpace(_config.AllowedUsers))
        {
            var allowed = _config.AllowedUsers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (allowed.Contains(source.UserId, StringComparer.OrdinalIgnoreCase))
                return true;
        }

        // Tier 4: Per-platform allowlist
        if (_config.Platforms.TryGetValue(source.Platform, out var platConfig) &&
            platConfig.Extra.TryGetValue("allowed_users", out var platAllowed))
        {
            var allowed = platAllowed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (allowed.Contains(source.UserId, StringComparer.OrdinalIgnoreCase))
                return true;
        }

        // Tier 5: DMs allowed by default when no allowlist is configured
        if (source.IsDm && string.IsNullOrWhiteSpace(_config.AllowedUsers))
            return true;

        return false;
    }

    // ══════════════════════════════════════════
    // Session Management
    // ══════════════════════════════════════════

    private string BuildSessionKey(SessionSource source)
    {
        // Upstream: platform + chat_id + optional user grouping
        var key = $"{source.Platform}:{source.ChatId}";
        if (_config.GroupSessionsPerUser && source.IsGroup)
            key += $":{source.UserId}";
        return key;
    }

    private static string BuildAgentMessage(MessageEvent evt)
    {
        if (evt.MediaUrls.Count == 0)
            return evt.Text;

        var mediaSummary = string.Join(", ", evt.MediaUrls.Select((url, index) =>
        {
            var type = index < evt.MediaTypes.Count ? evt.MediaTypes[index] : "media";
            return $"{type}: {url}";
        }));

        var baseText = string.IsNullOrWhiteSpace(evt.Text) ? "(no text)" : evt.Text;
        return $"{baseText}\n\n[Telegram attachments]\n{mediaSummary}";
    }

    // ══════════════════════════════════════════
    // Background Watchers
    // ══════════════════════════════════════════

    /// <summary>
    /// Flush expired sessions every 5 minutes.
    /// Upstream: _session_expiry_watcher
    /// </summary>
    private async Task SessionExpiryWatcherAsync(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct);

            var now = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(_config.SessionTimeoutSeconds);

            foreach (var (key, entry) in _sessions)
            {
                if (now - entry.LastActivity > timeout)
                {
                    _sessions.TryRemove(key, out _);
                    _logger.LogDebug("Expired session: {Key}", key);
                }
            }
        }
    }

    /// <summary>
    /// Retry failed platform connections with exponential backoff.
    /// Upstream: _platform_reconnect_watcher (30s → 300s cap, max 20 retries)
    /// </summary>
    private async Task PlatformReconnectWatcherAsync(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            foreach (var (platform, info) in _failedPlatforms)
            {
                if (DateTime.UtcNow < info.NextRetry) continue;
                if (info.Attempts >= _config.MaxRetryAttempts)
                {
                    _logger.LogWarning("Giving up on {Platform} after {Attempts} attempts", platform, info.Attempts);
                    _failedPlatforms.TryRemove(platform, out _);
                    continue;
                }

                try
                {
                    var success = await info.Adapter.ConnectAsync(ct);
                    if (success)
                    {
                        _adapters[platform] = info.Adapter;
                        _failedPlatforms.TryRemove(platform, out _);
                        _logger.LogInformation("Reconnected to {Platform} after {Attempts} attempts",
                            platform, info.Attempts);
                    }
                    else
                    {
                        ScheduleRetry(info);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Retry failed for {Platform} (attempt {Attempt})",
                        platform, info.Attempts);
                    ScheduleRetry(info);
                }
            }
        }
    }

    private void QueueForRetry(IPlatformAdapter adapter)
    {
        var info = new FailedPlatformInfo
        {
            Adapter = adapter,
            Attempts = 1,
            NextRetry = DateTime.UtcNow.AddSeconds(30)
        };
        _failedPlatforms[adapter.Platform] = info;
    }

    private void ScheduleRetry(FailedPlatformInfo info)
    {
        info.Attempts++;
        // Exponential backoff: 30s → 60s → 120s → capped at 300s
        var backoff = Math.Min(30 * (1 << (info.Attempts - 1)), 300);
        info.NextRetry = DateTime.UtcNow.AddSeconds(backoff);
    }

    private void OnPlatformError(Platform platform, Exception ex)
    {
        _logger.LogError(ex, "Fatal error on {Platform}", platform);
        if (_adapters.TryRemove(platform, out var adapter))
            QueueForRetry(adapter);
    }

    // ══════════════════════════════════════════
    // Shutdown
    // ══════════════════════════════════════════

    public async Task StopAsync()
    {
        _running = false;
        _logger.LogInformation("Gateway shutting down — disconnecting {Count} platforms", _adapters.Count);

        foreach (var (platform, adapter) in _adapters)
        {
            try
            {
                await adapter.DisconnectAsync();
                _logger.LogDebug("Disconnected from {Platform}", platform);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disconnecting from {Platform}", platform);
            }
        }

        _adapters.Clear();
        _sessions.Clear();
        _activeAgents.Clear();
        _lastChatIds.Clear();
    }
}

// ── Internal types ──

internal sealed class SessionEntry
{
    public required string SessionId { get; init; }
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
}

internal sealed class FailedPlatformInfo
{
    public required IPlatformAdapter Adapter { get; init; }
    public int Attempts { get; set; }
    public DateTime NextRetry { get; set; }
}
