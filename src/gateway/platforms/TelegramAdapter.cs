namespace Hermes.Agent.Gateway.Platforms;

using System.Net.Http.Json;
using System.Text.Json;
using Hermes.Agent.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// ══════════════════════════════════════════════
// Telegram Bot API Adapter
// ══════════════════════════════════════════════
//
// Uses Telegram Bot API via HTTP long-polling (getUpdates).
// No external NuGet needed — pure HttpClient.
// Upstream ref: gateway/platforms/telegram.py

/// <summary>
/// Telegram platform adapter using Bot API with long-polling.
/// Supports text messages, commands, and media placeholders.
/// </summary>
public sealed class TelegramAdapter : IPlatformAdapter
{
    private readonly string _token;
    private readonly HttpClient _http;
    private readonly ILogger<TelegramAdapter> _logger;
    private Func<MessageEvent, Task<string?>>? _messageHandler;
    private Action<Platform, Exception>? _errorHandler;
    private CancellationTokenSource? _pollCts;
    private long _lastUpdateId;

    public TelegramAdapter(string token, HttpClient? http = null, ILogger<TelegramAdapter>? logger = null)
    {
        _token = token;
        _http = http ?? new HttpClient();
        _logger = logger ?? NullLogger<TelegramAdapter>.Instance;
    }

    public Platform Platform => Platform.Telegram;
    public bool IsConnected { get; private set; }

    private string ApiUrl(string method) => $"https://api.telegram.org/bot{_token}/{method}";
    private string FileUrl(string filePath) => $"https://api.telegram.org/file/bot{_token}/{filePath}";

    public async Task<bool> ConnectAsync(CancellationToken ct)
    {
        try
        {
            // Verify token by calling getMe
            var response = await _http.GetAsync(ApiUrl("getMe"), ct);
            if (!response.IsSuccessStatusCode) return false;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.GetProperty("ok").GetBoolean()) return false;

            IsConnected = true;

            // Start polling loop
            _pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(() => PollUpdatesAsync(_pollCts.Token), _pollCts.Token);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram adapter connect failed");
            return false;
        }
    }

    public async Task<DeliveryResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        try
        {
            // Chunk long messages (Telegram limit: 4096 chars)
            var text = message.Text;
            var chunks = ChunkText(text, 4096);

            string? lastMessageId = null;
            foreach (var chunk in chunks)
            {
                var payload = new
                {
                    chat_id = message.ChatId,
                    text = chunk,
                    reply_to_message_id = message.ReplyToMessageId
                };

                var response = await _http.PostAsJsonAsync(ApiUrl("sendMessage"), payload, ct);
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.GetProperty("ok").GetBoolean())
                {
                    lastMessageId = doc.RootElement.GetProperty("result")
                        .GetProperty("message_id").GetInt64().ToString();
                }
                else
                {
                    var desc = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : "Unknown error";
                    return DeliveryResult.Fail($"Telegram API error: {desc}");
                }
            }

            return DeliveryResult.Ok(lastMessageId);
        }
        catch (Exception ex)
        {
            return DeliveryResult.Fail(ex.Message);
        }
    }

    public async Task DisconnectAsync()
    {
        BestEffort.Run(() => _pollCts?.Cancel(), _logger, "stopping Telegram polling");
        IsConnected = false;
        await Task.CompletedTask;
    }

    public void SetMessageHandler(Func<MessageEvent, Task<string?>> handler) => _messageHandler = handler;
    public void SetErrorHandler(Action<Platform, Exception> handler) => _errorHandler = handler;

    public ValueTask DisposeAsync()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── Long-polling loop ──

    private async Task PollUpdatesAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = ApiUrl($"getUpdates?offset={_lastUpdateId + 1}&timeout=30");
                var response = await _http.GetAsync(url, ct);
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.GetProperty("ok").GetBoolean()) continue;

                var results = doc.RootElement.GetProperty("result");
                foreach (var update in results.EnumerateArray())
                {
                    _lastUpdateId = update.GetProperty("update_id").GetInt64();

                    if (!update.TryGetProperty("message", out var msg)) continue;

                    var chat = msg.GetProperty("chat");
                    var chatId = chat.GetProperty("id").GetInt64().ToString();
                    var chatType = chat.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : "private";
                    var from = msg.TryGetProperty("from", out var fromEl) ? fromEl : default;
                    var userId = from.ValueKind == JsonValueKind.Object && from.TryGetProperty("id", out var uidEl)
                        ? uidEl.GetInt64().ToString() : "unknown";
                    var username = from.ValueKind == JsonValueKind.Object && from.TryGetProperty("username", out var unEl)
                        ? unEl.GetString() : null;

                    var mediaUrls = new List<string>();
                    var mediaTypes = new List<string>();
                    var text = await ExtractMessageTextAsync(msg, ct, mediaUrls, mediaTypes);
                    if (string.IsNullOrWhiteSpace(text))
                        text = mediaUrls.Count > 0 ? $"[{mediaTypes.FirstOrDefault() ?? "media"}]" : "";

                    var evt = new MessageEvent
                    {
                        Text = text,
                        Type = mediaTypes.Count > 0
                            ? mediaTypes[0] switch
                            {
                                "photo" => MessageType.Image,
                                "voice" or "audio" => MessageType.Audio,
                                "document" => MessageType.Document,
                                "video" => MessageType.Video,
                                _ => MessageType.Text
                            }
                            : (text.StartsWith('/') ? MessageType.Command : MessageType.Text),
                        Source = new SessionSource
                        {
                            Platform = Platform.Telegram,
                            ChatId = chatId,
                            UserId = userId,
                            Username = username,
                            IsGroup = chatType is "group" or "supergroup",
                            IsDm = chatType == "private"
                        },
                        MediaUrls = mediaUrls,
                        MediaTypes = mediaTypes,
                        MessageId = msg.TryGetProperty("message_id", out var midEl) ? midEl.GetInt64().ToString() : null
                    };

                    if (_messageHandler is not null)
                    {
                        await SendChatActionAsync(chatId, "typing", ct);
                        var reply = await _messageHandler(evt);
                        if (!string.IsNullOrWhiteSpace(reply))
                        {
                            await SendAsync(new OutboundMessage
                            {
                                Platform = Platform.Telegram,
                                ChatId = chatId,
                                Text = reply,
                                ReplyToMessageId = evt.MessageId
                            }, ct);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telegram adapter polling failed; retrying");
                _errorHandler?.Invoke(Platform.Telegram, ex);
                await Task.Delay(5000, ct); // Brief pause before retrying
            }
        }
    }

    private async Task SendChatActionAsync(string chatId, string action, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                chat_id = chatId,
                action
            };

            await _http.PostAsJsonAsync(ApiUrl("sendChatAction"), payload, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Telegram chat action failed for {ChatId}", chatId);
        }
    }

    private static List<string> ChunkText(string text, int maxLength)
    {
        if (text.Length <= maxLength) return new List<string> { text };

        var chunks = new List<string>();
        var remaining = text.AsSpan();
        while (remaining.Length > 0)
        {
            var len = Math.Min(maxLength, remaining.Length);
            // Try to break at newline
            if (len < remaining.Length)
            {
                var lastNewline = remaining[..len].LastIndexOf('\n');
                if (lastNewline > len / 2) len = lastNewline + 1;
            }
            chunks.Add(remaining[..len].ToString());
            remaining = remaining[len..];
        }
        return chunks;
    }

    private async Task<string> ExtractMessageTextAsync(
        JsonElement msg,
        CancellationToken ct,
        List<string> mediaUrls,
        List<string> mediaTypes)
    {
        if (msg.TryGetProperty("text", out var textEl))
            return textEl.GetString() ?? "";

        if (msg.TryGetProperty("caption", out var captionEl))
            return captionEl.GetString() ?? "";

        if (msg.TryGetProperty("photo", out var photoEl) &&
            photoEl.ValueKind == JsonValueKind.Array &&
            photoEl.GetArrayLength() > 0)
        {
            var photo = photoEl.EnumerateArray().Last();
            if (photo.TryGetProperty("file_id", out var fileIdEl))
            {
                var url = await DownloadTelegramFileAsync(fileIdEl.GetString(), "jpg", ct);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    mediaUrls.Add(url);
                    mediaTypes.Add("photo");
                }
            }
            return msg.TryGetProperty("caption", out var captionFromPhoto) ? captionFromPhoto.GetString() ?? "" : "";
        }

        if (msg.TryGetProperty("voice", out var voiceEl) && voiceEl.ValueKind == JsonValueKind.Object)
        {
            if (voiceEl.TryGetProperty("file_id", out var fileIdEl))
            {
                var url = await DownloadTelegramFileAsync(fileIdEl.GetString(), "ogg", ct);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    mediaUrls.Add(url);
                    mediaTypes.Add("voice");
                }
            }
            return "";
        }

        if (msg.TryGetProperty("audio", out var audioEl) && audioEl.ValueKind == JsonValueKind.Object)
        {
            if (audioEl.TryGetProperty("file_id", out var fileIdEl))
            {
                var url = await DownloadTelegramFileAsync(fileIdEl.GetString(), "mp3", ct);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    mediaUrls.Add(url);
                    mediaTypes.Add("audio");
                }
            }
            return "";
        }

        if (msg.TryGetProperty("document", out var documentEl) && documentEl.ValueKind == JsonValueKind.Object)
        {
            if (documentEl.TryGetProperty("file_id", out var fileIdEl))
            {
                var url = await DownloadTelegramFileAsync(fileIdEl.GetString(), "bin", ct);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    mediaUrls.Add(url);
                    mediaTypes.Add("document");
                }
            }
            return "";
        }

        if (msg.TryGetProperty("video", out var videoEl) && videoEl.ValueKind == JsonValueKind.Object)
        {
            if (videoEl.TryGetProperty("file_id", out var fileIdEl))
            {
                var url = await DownloadTelegramFileAsync(fileIdEl.GetString(), "mp4", ct);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    mediaUrls.Add(url);
                    mediaTypes.Add("video");
                }
            }
            return "";
        }

        return "";
    }

    private async Task<string?> DownloadTelegramFileAsync(string? fileId, string extension, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileId))
            return null;

        try
        {
            var response = await _http.GetAsync(ApiUrl($"getFile?file_id={Uri.EscapeDataString(fileId)}"), ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.GetProperty("ok").GetBoolean())
                return null;

            var result = doc.RootElement.GetProperty("result");
            if (!result.TryGetProperty("file_path", out var pathEl))
                return null;

            var filePath = pathEl.GetString();
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            var fileBytes = await _http.GetByteArrayAsync(FileUrl(filePath), ct);
            var tempDir = Path.Combine(Path.GetTempPath(), "HermesTelegram");
            Directory.CreateDirectory(tempDir);
            var safeName = $"{Guid.NewGuid():N}.{extension}";
            var localPath = Path.Combine(tempDir, safeName);
            await File.WriteAllBytesAsync(localPath, fileBytes, ct);
            return localPath;
        }
        catch
        {
            return null;
        }
    }
}
