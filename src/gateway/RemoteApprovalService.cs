namespace Hermes.Agent.Gateway;

using System.Collections.Concurrent;
using System.Text;

public enum RemoteApprovalDecision
{
    Deny = 0,
    AllowOnce = 1,
    AlwaysAllowTool = 2,
}

/// <summary>
/// Tracks permission prompts that can be approved or denied remotely.
/// The desktop permission callback registers a pending request here, and
/// the gateway checks incoming Telegram messages for matching allow/deny
/// commands before routing them to the agent.
/// </summary>
public sealed class RemoteApprovalService
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new();

    public PendingApproval CreateRequest(
        Platform platform,
        string chatId,
        string toolName,
        string message,
        string? toolArguments)
    {
        var request = new PendingApproval(
            id: Guid.NewGuid().ToString("N")[..8],
            platform,
            chatId,
            toolName,
            message,
            toolArguments);

        _pending[request.Id] = request;
        return request;
    }

    public bool TryCompleteFromMessage(MessageEvent evt, out string responseText)
    {
        responseText = string.Empty;
        if (evt.Source.Platform != Platform.Telegram)
            return false;

        var text = evt.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (!TryParseCommand(text, out var decision, out var requestId))
            return false;

        var matches = _pending.Values
            .Where(p => p.Platform == evt.Source.Platform && p.ChatId == evt.Source.ChatId)
            .Where(p => string.IsNullOrWhiteSpace(requestId) || string.Equals(p.Id, requestId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.CreatedAtUtc)
            .ToList();

        if (matches.Count == 0)
            return false;

        var request = matches[0];
        if (_pending.TryRemove(request.Id, out _))
        {
            request.TrySetResult(decision);
            responseText = BuildConfirmationText(request, decision);
            return true;
        }

        return false;
    }

    public bool TryGetRequest(string requestId, out PendingApproval request) =>
        _pending.TryGetValue(requestId, out request!);

    public string BuildPrompt(PendingApproval request)
    {
        var summary = request.FormatSummary();
        return
            $"Permission required: {request.ToolName}\n\n" +
            $"{summary}\n\n" +
            $"Reply with /allow {request.Id}, /always {request.Id}, or /deny {request.Id}.";
    }

    public void Remove(string requestId) => _pending.TryRemove(requestId, out _);

    private static string BuildConfirmationText(PendingApproval request, RemoteApprovalDecision decision) =>
        decision switch
        {
            RemoteApprovalDecision.AllowOnce => $"Approved once: {request.ToolName} ({request.Id})",
            RemoteApprovalDecision.AlwaysAllowTool => $"Always allowed: {request.ToolName} ({request.Id})",
            _ => $"Denied: {request.ToolName} ({request.Id})"
        };

    private static bool TryParseCommand(string text, out RemoteApprovalDecision decision, out string? requestId)
    {
        decision = RemoteApprovalDecision.Deny;
        requestId = null;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        var command = parts[0].TrimStart('/').ToLowerInvariant();
        decision = command switch
        {
            "allow" => RemoteApprovalDecision.AllowOnce,
            "always" => RemoteApprovalDecision.AlwaysAllowTool,
            "deny" => RemoteApprovalDecision.Deny,
            _ => RemoteApprovalDecision.Deny
        };

        if (command is not ("allow" or "always" or "deny"))
            return false;

        requestId = parts.Length > 1 ? parts[1] : null;
        return true;
    }

    public sealed class PendingApproval
    {
        private readonly TaskCompletionSource<RemoteApprovalDecision> _tcsRemote =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal PendingApproval(string id, Platform platform, string chatId, string toolName, string message, string? toolArguments)
        {
            Id = id;
            Platform = platform;
            ChatId = chatId;
            ToolName = toolName;
            Message = message;
            ToolArguments = toolArguments;
            CreatedAtUtc = DateTime.UtcNow;
        }

        public string Id { get; }
        public Platform Platform { get; }
        public string ChatId { get; }
        public string ToolName { get; }
        public string Message { get; }
        public string? ToolArguments { get; }
        public DateTime CreatedAtUtc { get; }
        public Task<RemoteApprovalDecision> DecisionTask => _tcsRemote.Task;

        public bool TrySetResult(RemoteApprovalDecision decision) => _tcsRemote.TrySetResult(decision);

        public string FormatSummary()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"Chat: {ChatId}");
            builder.AppendLine($"Request: {Id}");
            builder.AppendLine();
            builder.AppendLine(Message);

            if (!string.IsNullOrWhiteSpace(ToolArguments))
            {
                builder.AppendLine();
                builder.AppendLine("Arguments:");
                builder.AppendLine(ToolArguments);
            }

            return builder.ToString().Trim();
        }
    }
}
