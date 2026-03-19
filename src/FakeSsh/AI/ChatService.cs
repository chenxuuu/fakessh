using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FakeSsh.Config;
using Microsoft.Extensions.Options;

namespace FakeSsh.AI;

public class ChatService
{
    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly ILogger<ChatService> _logger;

    public ChatService(IOptions<AppConfig> config, ILogger<ChatService> logger)
    {
        _config = config.Value;
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3)
        };

        var baseUrl = _config.OpenAi.BaseUrl.TrimEnd('/');
        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _config.OpenAi.ApiKey);
    }

    /// <summary>
    /// Stream chat completions from OpenAI API.
    /// Returns chunks of text as they arrive.
    /// </summary>
    public async IAsyncEnumerable<string> StreamChatAsync(
        string systemPrompt,
        string userCommand,
        List<AiMessage> conversationHistory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        // Add recent conversation for context (limit to avoid token overflow)
        foreach (var msg in conversationHistory.TakeLast(20))
        {
            messages.Add(new { role = msg.Role, content = msg.Content });
        }

        // Add the current command
        messages.Add(new { role = "user", content = userCommand });

        var body = new
        {
            model = _config.OpenAi.Model,
            messages,
            stream = true,
            temperature = 0.6,
            max_tokens = _config.OpenAi.MaxTokens,
            enable_thinking = false,
        };

        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        string? errorMessage = null;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = content
            };

            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("OpenAI API returned {Status}: {Body}",
                    (int)response.StatusCode, errorBody.Length > 500 ? errorBody[..500] : errorBody);
                errorMessage = "bash: internal error: bash service unavailable\n";
                response = null!;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI API request failed");
            errorMessage = "bash: internal error: bash service unavailable\n";
            response = null!;
        }

        if (errorMessage != null)
        {
            yield return errorMessage;
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line[6..];
            if (data == "[DONE]") break;

            ChatCompletionChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatCompletionChunk>(data);
            }
            catch
            {
                continue;
            }

            var delta = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
            if (!string.IsNullOrEmpty(delta))
            {
                yield return delta;
            }
        }
    }

    /// <summary>
    /// Non-streaming completion for simple/fast commands
    /// </summary>
    public async Task<string> CompleteChatAsync(
        string systemPrompt,
        string userCommand,
        List<AiMessage> conversationHistory,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in StreamChatAsync(systemPrompt, userCommand, conversationHistory, ct))
        {
            sb.Append(chunk);
        }
        return sb.ToString();
    }
}

public class AiMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
}

#region OpenAI Response Models

public class ChatCompletionChunk
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("choices")]
    public List<ChunkChoice>? Choices { get; set; }
}

public class ChunkChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("delta")]
    public ChunkDelta? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public class ChunkDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }
}

#endregion
