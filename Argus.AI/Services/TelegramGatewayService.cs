using System.Text.Json;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Argus.Core.Services;

namespace Argus.AI.Services;

public sealed class TelegramGatewayService : ITelegramGatewayService
{
    private readonly ISettingsService _settingsService;
    private readonly ISecretStore _secretStore;
    private readonly IAgentService _agentService;
    private readonly IConversationService _conversationService;
    private readonly HttpClient _httpClient;
    private readonly List<string> _logs = new();
    private readonly object _logLock = new();

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _isRunning;
    private string _status = "Stopped";

    public bool IsRunning => _isRunning;
    public string Status => _status;
    public event Action? OnStatusChanged;

    public TelegramGatewayService(
        ISettingsService settingsService,
        ISecretStore secretStore,
        IAgentService agentService,
        IConversationService conversationService,
        HttpClient httpClient)
    {
        _settingsService = settingsService;
        _secretStore = secretStore;
        _agentService = agentService;
        _conversationService = conversationService;
        _httpClient = httpClient;
    }

    public string GetLogs()
    {
        lock (_logLock)
        {
            return string.Join(Environment.NewLine, _logs);
        }
    }

    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var logLine = $"[{timestamp}] {message}";
        lock (_logLock)
        {
            _logs.Add(logLine);
            if (_logs.Count > 100)
            {
                _logs.RemoveAt(0);
            }
        }
        OnStatusChanged?.Invoke();
    }

    private void SetStatus(string status)
    {
        _status = status;
        OnStatusChanged?.Invoke();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
        {
            await StopAsync();
        }

        var enabledStr = await _settingsService.GetSettingAsync("TelegramBotEnabled", "false", cancellationToken);
        if (!bool.TryParse(enabledStr, out var enabled) || !enabled)
        {
            SetStatus("Disabled");
            Log("Telegram Gateway is disabled in settings.");
            return;
        }

        var token = await _secretStore.GetSecretAsync("telegram.bot_token", cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            SetStatus("Error: Missing Token");
            Log("Telegram Gateway cannot start: token is empty.");
            return;
        }

        var useWebhookStr = await _settingsService.GetSettingAsync("TelegramUseWebhook", "false", cancellationToken);
        var useWebhook = bool.TryParse(useWebhookStr, out var uw) && uw;

        _isRunning = true;
        _cts = new CancellationTokenSource();

        if (useWebhook)
        {
            var webhookUrl = await _settingsService.GetSettingAsync("TelegramWebhookUrl", "", cancellationToken) ?? "";
            var portStr = await _settingsService.GetSettingAsync("TelegramWebhookPort", "8080", cancellationToken) ?? "8080";
            if (!int.TryParse(portStr, out var port)) port = 8080;

            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                SetStatus("Error: Missing Webhook URL");
                Log("Telegram Gateway cannot start in webhook mode: Webhook URL is empty.");
                _isRunning = false;
                _cts.Dispose();
                _cts = null;
                return;
            }

            _runTask = Task.Run(() => WebhookLoopAsync(token, webhookUrl, port, _cts.Token));
            SetStatus("Running (Webhook)");
            Log("Telegram Gateway starting in Webhook mode...");
        }
        else
        {
            _runTask = Task.Run(() => PollingLoopAsync(token, _cts.Token));
            SetStatus("Running");
            Log("Telegram Gateway starting in Polling mode...");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_runTask is not null)
        {
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"Error during shutdown: {ex.Message}");
            }
        }

        _cts?.Dispose();
        _cts = null;
        _runTask = null;
        SetStatus("Stopped");
        Log("Telegram Gateway stopped.");
    }

    private async Task PollingLoopAsync(string token, CancellationToken ct)
    {
        long offset = 0;
        var baseUrl = $"https://api.telegram.org/bot{token}";

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Retrieve allowed users dynamically in the loop to support real-time settings updates
                var allowedUsersStr = await _settingsService.GetSettingAsync("TelegramAllowedUserIds", "", ct) ?? "";
                var allowedUsers = allowedUsersStr
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var response = await _httpClient.GetAsync($"{baseUrl}/getUpdates?offset={offset}&timeout=5", ct);
                if (!response.IsSuccessStatusCode)
                {
                    Log($"Telegram API returned status: {response.StatusCode}");
                    await Task.Delay(5000, ct);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
                {
                    Log("Telegram API ok=false");
                    await Task.Delay(5000, ct);
                    continue;
                }

                var result = doc.RootElement.GetProperty("result");
                foreach (var update in result.EnumerateArray())
                {
                    var updateId = update.GetProperty("update_id").GetInt64();
                    offset = updateId + 1;

                    if (update.TryGetProperty("message", out var message))
                    {
                        await ProcessMessageAsync(baseUrl, message, allowedUsers, ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Polling error: {ex.Message}");
                try
                {
                    await Task.Delay(5000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task WebhookLoopAsync(string token, string webhookUrl, int port, CancellationToken ct)
    {
        var baseUrl = $"https://api.telegram.org/bot{token}";

        // Register webhook with Telegram
        Log($"Setting Telegram webhook to: {webhookUrl}...");
        try
        {
            var response = await _httpClient.GetAsync($"{baseUrl}/setWebhook?url={webhookUrl}", ct);
            if (response.IsSuccessStatusCode)
            {
                Log("Telegram webhook registered successfully.");
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                Log($"Failed to register webhook: {response.StatusCode} - {body}");
            }
        }
        catch (Exception ex)
        {
            Log($"Error registering webhook: {ex.Message}");
        }

        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try
        {
            listener.Start();
            Log($"Webhook server listening on http://localhost:{port}/");
        }
        catch (Exception ex)
        {
            Log($"HttpListener start error: {ex.Message}. Make sure the port is not in use.");
            SetStatus("Error: Webhook Start Failed");
            return;
        }

        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            try
            {
                var contextTask = listener.GetContextAsync();
                var completedTask = await Task.WhenAny(contextTask, Task.Delay(-1, ct));
                if (completedTask != contextTask)
                {
                    // Cancelled
                    break;
                }

                var context = await contextTask;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var request = context.Request;
                        var response = context.Response;

                        // Read allowed users whitelists dynamically
                        var allowedUsersStr = await _settingsService.GetSettingAsync("TelegramAllowedUserIds", "", ct) ?? "";
                        var allowedUsers = allowedUsersStr
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        if (request.HttpMethod == "POST")
                        {
                            using var reader = new System.IO.StreamReader(request.InputStream);
                            var jsonStr = await reader.ReadToEndAsync();

                            using var doc = JsonDocument.Parse(jsonStr);
                            var root = doc.RootElement;

                            if (root.TryGetProperty("message", out var message))
                            {
                                await ProcessMessageAsync(baseUrl, message, allowedUsers, ct);
                            }

                            response.StatusCode = 200;
                            response.StatusDescription = "OK";
                        }
                        else
                        {
                            response.StatusCode = 405; // Method not allowed
                        }
                        response.Close();
                    }
                    catch (Exception ex)
                    {
                        Log($"Error handling webhook request: {ex.Message}");
                    }
                }, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                Log($"Webhook error: {ex.Message}");
            }
        }

        // Cleanup: delete webhook
        try
        {
            listener.Stop();
            Log("Webhook server stopped.");

            Log("Deleting Telegram webhook...");
            var response = await _httpClient.GetAsync($"{baseUrl}/deleteWebhook", CancellationToken.None);
            if (response.IsSuccessStatusCode)
            {
                Log("Telegram webhook deleted.");
            }
        }
        catch (Exception ex)
        {
            Log($"Error deleting webhook: {ex.Message}");
        }
    }

    private async Task ProcessMessageAsync(string baseUrl, JsonElement message, HashSet<string> allowedUsers, CancellationToken ct)
    {
        if (!message.TryGetProperty("text", out var textProp) || string.IsNullOrWhiteSpace(textProp.GetString()))
        {
            return;
        }

        var text = textProp.GetString()!.Trim();
        var chatId = message.GetProperty("chat").GetProperty("id").GetInt64();

        var from = message.GetProperty("from");
        var userId = from.GetProperty("id").GetInt64().ToString();
        var username = from.TryGetProperty("username", out var userProp) ? userProp.GetString() : null;

        Log($"Received message from user ID {userId} (@{username ?? "none"}): {text}");

        // Security check
        var isAllowed = allowedUsers.Count > 0 &&
                        (allowedUsers.Contains(userId) ||
                        (!string.IsNullOrEmpty(username) && allowedUsers.Contains(username)));

        if (!isAllowed)
        {
            Log($"Security Warning: Blocked unauthorized message from user ID {userId} (@{username ?? "none"})");
            await SendMessageAsync(baseUrl, chatId, "Unauthorized: Access denied. Please configure allowed user IDs in Settings.", ct);
            return;
        }

        // 1. Get or Create Telegram conversation ID (Isolated by chatId)
        Guid activeConversationId;
        var settingKey = $"TelegramActiveConv_{chatId}";
        var activeConvIdStr = await _settingsService.GetSettingAsync(settingKey, "", ct);
        if (Guid.TryParse(activeConvIdStr, out var existingGuid))
        {
            activeConversationId = existingGuid;
        }
        else
        {
            var chat = message.GetProperty("chat");
            var chatType = chat.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : "private";
            var chatTitle = chat.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
            var firstName = chat.TryGetProperty("first_name", out var fnProp) ? fnProp.GetString() : null;
            var lastName = chat.TryGetProperty("last_name", out var lnProp) ? lnProp.GetString() : null;

            string convTitle;
            if (chatType == "private")
            {
                var fullName = string.Join(" ", new[] { firstName, lastName }.Where(s => !string.IsNullOrEmpty(s)));
                var identifier = !string.IsNullOrEmpty(fullName) ? fullName : (!string.IsNullOrEmpty(username) ? $"@{username}" : $"User {userId}");
                convTitle = $"Telegram Chat: {identifier}";
            }
            else
            {
                convTitle = $"Telegram Group: {chatTitle ?? chatId.ToString()}";
            }

            var newConv = await _conversationService.CreateConversationAsync(convTitle, ct);
            activeConversationId = newConv.Id;
            await _settingsService.SaveSettingAsync(settingKey, activeConversationId.ToString(), ct);
        }

        // 2. Handle commands
        if (text.Equals("/new", StringComparison.OrdinalIgnoreCase))
        {
            var chat = message.GetProperty("chat");
            var chatType = chat.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : "private";
            var chatTitle = chat.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
            var firstName = chat.TryGetProperty("first_name", out var fnProp) ? fnProp.GetString() : null;
            var lastName = chat.TryGetProperty("last_name", out var lnProp) ? lnProp.GetString() : null;

            string convTitle;
            if (chatType == "private")
            {
                var fullName = string.Join(" ", new[] { firstName, lastName }.Where(s => !string.IsNullOrEmpty(s)));
                var identifier = !string.IsNullOrEmpty(fullName) ? fullName : (!string.IsNullOrEmpty(username) ? $"@{username}" : $"User {userId}");
                convTitle = $"Telegram Chat: {identifier}";
            }
            else
            {
                convTitle = $"Telegram Group: {chatTitle ?? chatId.ToString()}";
            }

            // Reset session: Create new conversation
            var newConv = await _conversationService.CreateConversationAsync(convTitle, ct);
            activeConversationId = newConv.Id;
            await _settingsService.SaveSettingAsync(settingKey, activeConversationId.ToString(), ct);

            // Read model info
            var profile = await _settingsService.GetDefaultAiProviderProfileAsync(ct);
            var modeName = profile?.Model ?? "unknown";
            var providerName = profile?.Name ?? profile?.ProviderType ?? "unknown";
            var contextTokens = profile is null
                ? null
                : AiModelCatalog.FindKnownModel(profile.ProviderType, profile.BaseUrl, profile.Model)?.ContextWindowTokens;
            var contextLimit = contextTokens.HasValue
                ? $"{AiModelCatalog.FormatTokenCount(contextTokens)} tokens"
                : "unknown";

            var response =
                $"🆕 Session reset! Starting fresh.\n\n" +
                $"◆ Mode: {modeName}\n" +
                $"◆ Provider: {providerName}\n" +
                $"◆ Context: {contextLimit}\n" +
                $"◆ Tip: /undo removes the last user/assistant exchange from the conversation.";

            Log($"Session reset for chat {chatId}. Created conversation {activeConversationId}");
            await SendMessageAsync(baseUrl, chatId, response, ct);
            return;
        }

        if (text.Equals("/undo", StringComparison.OrdinalIgnoreCase))
        {
            // Load messages for the conversation, locate last user and assistant messages, and delete them.
            var dbMessages = await _conversationService.GetMessagesAsync(activeConversationId, ct);
            if (dbMessages.Count == 0)
            {
                await SendMessageAsync(baseUrl, chatId, "Nothing to undo: conversation is empty.", ct);
                return;
            }

            // Find last user and assistant messages
            var userMsg = dbMessages.LastOrDefault(m => m.Role == "user");
            var assistantMsg = dbMessages.LastOrDefault(m => m.Role == "assistant");

            int deletedCount = 0;
            if (assistantMsg is not null)
            {
                await _conversationService.DeleteMessageAsync(assistantMsg.Id, ct);
                deletedCount++;
            }
            if (userMsg is not null)
            {
                await _conversationService.DeleteMessageAsync(userMsg.Id, ct);
                deletedCount++;
            }

            string undoResponse = deletedCount > 0
                ? $"↩️ Undid the last user/assistant exchange."
                : "Nothing to undo.";
            Log($"Undid last exchange in conversation {activeConversationId} for chat {chatId}. Deleted {deletedCount} messages.");
            await SendMessageAsync(baseUrl, chatId, undoResponse, ct);
            return;
        }

        // 3. Normal messages flow
        try
        {
            Log($"Running agent instruction for user {userId} in conversation {activeConversationId}...");

            // Save user message in the database conversation first!
            await _conversationService.AddMessageAsync(activeConversationId, "user", text, null, ct);

            var (finalAnswer, logText) = await _agentService.RunWithDetailsAsync(text, activeConversationId, ct);

            Log($"Agent instruction completed for user {userId}.");

            // Save assistant message in the database conversation!
            await _conversationService.AddMessageAsync(activeConversationId, "assistant", finalAnswer, null, ct);

            // Send the clean final assistant response by default. The execution log is opt-in.
            var showThinkingStr = await _settingsService.GetSettingAsync("ShowThinkingInTelegram", "false", ct);
            var showThinking = bool.TryParse(showThinkingStr, out var enabled) && enabled;
            var outgoing = showThinking
                ? $"{finalAnswer}{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}Thinking{Environment.NewLine}{logText}"
                : finalAnswer;
            await SendMessageAsync(baseUrl, chatId, outgoing, ct);
        }
        catch (Exception ex)
        {
            Log($"Error executing agent: {ex.Message}");
            await SendMessageAsync(baseUrl, chatId, $"Error: {ex.Message}", ct);
        }
    }

    private async Task SendMessageAsync(string baseUrl, long chatId, string text, CancellationToken ct)
    {
        try
        {
            var htmlFormattedText = EscapeAndFormatToHtml(text);

            if (htmlFormattedText.Length > 4000)
            {
                htmlFormattedText = htmlFormattedText[..3900] + "\n...(truncated)";
            }

            var payload = new Dictionary<string, object>
            {
                ["chat_id"] = chatId,
                ["text"] = htmlFormattedText,
                ["parse_mode"] = "HTML"
            };

            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/sendMessage", payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                Log($"Failed to send message: {response.StatusCode} - {body}");

                // Fallback to sending plain escaped text if parsing failed due to tag mismatch
                if (body.Contains("can't parse entities"))
                {
                    Log("Retrying message send with plain escaped text fallback...");
                    var fallbackPayload = new Dictionary<string, object>
                    {
                        ["chat_id"] = chatId,
                        ["text"] = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    };
                    await _httpClient.PostAsJsonAsync($"{baseUrl}/sendMessage", fallbackPayload, ct);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error sending message: {ex.Message}");
        }
    }

    private static string EscapeAndFormatToHtml(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // First escape HTML special characters
        text = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        // We want to handle code blocks and inline code carefully so they are not formatted.
        // Replace code blocks with a placeholder.
        var codeBlocks = new List<string>();
        // Match ```lang\ncode``` or ```code```
        var codeBlockRegex = new Regex(@"```(?:(\w+)?\n)?([\s\S]*?)```");
        text = codeBlockRegex.Replace(text, m =>
        {
            var code = m.Groups[2].Value;
            var placeholder = $"CODEBLOCKPLACEHOLDER{codeBlocks.Count}";
            codeBlocks.Add($"<pre>{code}</pre>");
            return placeholder;
        });

        var inlineCodes = new List<string>();
        var inlineCodeRegex = new Regex(@"`([^`\n]+)`");
        text = inlineCodeRegex.Replace(text, m =>
        {
            var code = m.Groups[1].Value;
            var placeholder = $"INLINECODEPLACEHOLDER{inlineCodes.Count}";
            inlineCodes.Add($"<code>{code}</code>");
            return placeholder;
        });

        // Now format bold: **bold**
        var boldRegex = new Regex(@"\*\*([\s\S]*?)\*\*");
        text = boldRegex.Replace(text, "<b>$1</b>");

        // Format italic: *italic* or _italic_
        var italicRegex = new Regex(@"\*([\s\S]*?)\*");
        text = italicRegex.Replace(text, "<i>$1</i>");

        var italicUnderscoreRegex = new Regex(@"_([\s\S]*?)_");
        text = italicUnderscoreRegex.Replace(text, "<i>$1</i>");

        // Format links: [text](url)
        var linkRegex = new Regex(@"\[([\s\S]*?)\]\(([\s\S]*?)\)");
        text = linkRegex.Replace(text, m =>
        {
            var linkText = m.Groups[1].Value;
            var url = m.Groups[2].Value;
            return $"<a href=\"{url}\">{linkText}</a>";
        });

        // Replace placeholders back in reverse order (inline code first, then code blocks)
        for (int i = 0; i < inlineCodes.Count; i++)
        {
            text = text.Replace($"INLINECODEPLACEHOLDER{i}", inlineCodes[i]);
        }
        for (int i = 0; i < codeBlocks.Count; i++)
        {
            text = text.Replace($"CODEBLOCKPLACEHOLDER{i}", codeBlocks[i]);
        }

        return text;
    }
}
