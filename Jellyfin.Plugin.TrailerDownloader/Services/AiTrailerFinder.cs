using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.TrailerDownloader.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerDownloader.Services;

/// <summary>
/// Uses an LLM API (Anthropic or OpenAI, with their server-side web search tools) to find
/// official theatrical trailer links on YouTube. Returns an empty list on any failure so
/// the discovery pipeline can fall through to the next source.
/// </summary>
public partial class AiTrailerFinder
{
    private const string AnthropicDefaultModel = "claude-opus-4-8";
    private const string OpenAiDefaultModel = "gpt-5";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AiTrailerFinder> _logger;

    public AiTrailerFinder(IHttpClientFactory httpClientFactory, ILogger<AiTrailerFinder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> FindTrailerUrlsAsync(Movie movie, PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (config.AiProvider == AiProvider.None || string.IsNullOrWhiteSpace(config.AiApiKey))
        {
            return Array.Empty<string>();
        }

        var prompt = BuildPrompt(movie);

        try
        {
            var text = config.AiProvider switch
            {
                AiProvider.Anthropic => await QueryAnthropicAsync(prompt, config, cancellationToken).ConfigureAwait(false),
                AiProvider.OpenAi => await QueryOpenAiResponsesAsync(prompt, config, "https://api.openai.com", cancellationToken).ConfigureAwait(false),
                AiProvider.OpenAiCompatible => await QueryOpenAiChatAsync(prompt, config, cancellationToken).ConfigureAwait(false),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<string>();
            }

            var urls = ExtractYouTubeUrls(text);
            _logger.LogInformation("AI trailer search for {Movie} returned {Count} link(s)", movie.Name, urls.Count);
            return urls;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI trailer search failed for {Movie}", movie.Name);
            return Array.Empty<string>();
        }
    }

    private static string BuildPrompt(Movie movie)
    {
        var year = movie.ProductionYear?.ToString() ?? "unknown year";
        return
            $"Find the official theatrical trailer(s) on YouTube for the movie \"{movie.Name}\" ({year}). " +
            "Only include official theatrical trailers (studio/distributor uploads or faithful re-uploads of the " +
            "original theatrical trailer). Exclude fan edits, reviews, reaction videos, teasers labeled as TV spots, " +
            "and clips. If multiple distinct official theatrical trailers exist (e.g. Trailer 1 and Trailer 2, or a " +
            "restored 35mm/4K version), include each once. " +
            "Respond with ONLY a JSON array of YouTube watch URLs, most official/highest quality first, " +
            "for example: [\"https://www.youtube.com/watch?v=abc123\"]. If you cannot find any, respond with [].";
    }

    private async Task<string?> QueryAnthropicAsync(string prompt, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var model = string.IsNullOrWhiteSpace(config.AiModel) ? AnthropicDefaultModel : config.AiModel;
        var body = new
        {
            model,
            max_tokens = 2048,
            tools = new object[]
            {
                new { type = "web_search_20260209", name = "web_search", max_uses = 5 }
            },
            messages = new object[]
            {
                new { role = "user", content = prompt }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", config.AiApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var doc = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return null;
        }

        // Concatenate all text blocks from the response content.
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                    && block.TryGetProperty("text", out var text))
                {
                    sb.Append(text.GetString());
                }
            }
        }

        return sb.ToString();
    }

    private async Task<string?> QueryOpenAiResponsesAsync(string prompt, PluginConfiguration config, string baseUrl, CancellationToken cancellationToken)
    {
        var model = string.IsNullOrWhiteSpace(config.AiModel) ? OpenAiDefaultModel : config.AiModel;
        var body = new
        {
            model,
            tools = new object[] { new { type = "web_search" } },
            input = prompt
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/v1/responses");
        request.Headers.Add("Authorization", "Bearer " + config.AiApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var doc = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return null;
        }

        // Prefer the convenience output_text field; otherwise aggregate output[].content[].text.
        if (doc.RootElement.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString();
        }

        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(text.GetString());
                    }
                }
            }
        }

        return sb.ToString();
    }

    private async Task<string?> QueryOpenAiChatAsync(string prompt, PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.AiBaseUrl))
        {
            _logger.LogWarning("OpenAI-compatible provider selected but no base URL configured");
            return null;
        }

        var model = string.IsNullOrWhiteSpace(config.AiModel) ? OpenAiDefaultModel : config.AiModel;
        var body = new
        {
            model,
            messages = new object[] { new { role = "user", content = prompt } }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, config.AiBaseUrl.TrimEnd('/') + "/v1/chat/completions");
        request.Headers.Add("Authorization", "Bearer " + config.AiApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var doc = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return null;
        }

        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString();
                }
            }
        }

        return null;
    }

    private async Task<JsonDocument?> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "AI API returned {Status}: {Body}",
                (int)response.StatusCode,
                payload.Length > 500 ? payload[..500] : payload);
            return null;
        }

        return JsonDocument.Parse(payload);
    }

    /// <summary>Extracts YouTube watch URLs from the model output (JSON array preferred, free text tolerated).</summary>
    internal static IReadOnlyList<string> ExtractYouTubeUrls(string text)
    {
        var urls = new List<string>();

        // Try a JSON array first.
        var arrayMatch = JsonArrayRegex().Match(text);
        if (arrayMatch.Success)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(arrayMatch.Value);
                if (parsed is not null)
                {
                    urls.AddRange(parsed);
                }
            }
            catch (JsonException)
            {
                // fall through to free-text extraction
            }
        }

        if (urls.Count == 0)
        {
            foreach (Match m in YouTubeUrlRegex().Matches(text))
            {
                urls.Add(m.Value);
            }
        }

        return urls
            .Where(u => u.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase)
                     || u.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase))
            .Select(u => u.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [GeneratedRegex(@"\[[^\[\]]*\]", RegexOptions.Singleline)]
    private static partial Regex JsonArrayRegex();

    [GeneratedRegex(@"https?://(?:www\.)?(?:youtube\.com/watch\?v=[\w-]{6,}|youtu\.be/[\w-]{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeUrlRegex();
}
