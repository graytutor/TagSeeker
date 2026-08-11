using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CustomImageViewer.Services;

public sealed class OllamaTranslatorService
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<string> TranslateAsync(
        string text,
        string targetLanguage,
        string model,
        string endpoint,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("로컬 번역 모델 이름을 입력하세요.");

        var url = $"{NormalizeEndpoint(endpoint)}/api/chat";
        var payload = new
        {
            model = model.Trim(),
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = $"You are a precise translator. Translate the user's text into {targetLanguage}. " +
                              "Return only the translated text. Preserve line breaks, names, numbers, and punctuation. " +
                              "Do not explain, summarize, censor, or add notes."
                },
                new { role = "user", content = text }
            },
            stream = false,
            think = false,
            options = new { temperature = 0.1 }
        };

        using var response = await _httpClient.PostAsync(
            url,
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(ReadOllamaError(response.StatusCode, json, model));

        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("message").GetProperty("content").GetString()?.Trim() ?? string.Empty;
    }

    public async Task<IReadOnlyList<string>> GetInstalledModelsAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"{NormalizeEndpoint(endpoint)}/api/tags", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("models")
            .EnumerateArray()
            .Select(model => model.GetProperty("name").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeEndpoint(string endpoint) =>
        (string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434" : endpoint.Trim()).TrimEnd('/');

    private static string ReadOllamaError(System.Net.HttpStatusCode statusCode, string json, string model)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var error = document.RootElement.GetProperty("error").GetString();
            if (!string.IsNullOrWhiteSpace(error))
                return $"로컬 번역 오류 ({(int)statusCode}): {error}\n\n모델이 없다면 터미널에서 ollama pull {model} 을 실행하세요.";
        }
        catch { }
        return $"로컬 번역 오류 ({(int)statusCode})";
    }
}
