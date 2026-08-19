using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CustomImageViewer.Services;

public sealed class MyMemoryTranslatorService
{
    private const int MaximumQueryBytes = 480;
    private const int MaximumConcurrentRequests = 4;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _requestGate = new(MaximumConcurrentRequests, MaximumConcurrentRequests);

    public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var source = NormalizeLanguageCode(sourceLanguage);
        var target = NormalizeLanguageCode(targetLanguage);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return text;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var lineTasks = lines.Select(line => TranslateLineAsync(
            line, source, target, cancellationToken));
        var translatedLines = await Task.WhenAll(lineTasks);
        return string.Join(Environment.NewLine, translatedLines);
    }

    private async Task<string> TranslateLineAsync(
        string line,
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line)) return string.Empty;
        var partTasks = SplitByUtf8Bytes(line, MaximumQueryBytes)
            .Select(part => TranslatePartAsync(part, source, target, cancellationToken));
        return string.Concat(await Task.WhenAll(partTasks));
    }

    private async Task<string> TranslatePartAsync(
        string part,
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var url = "https://api.mymemory.translated.net/get" +
                      $"?q={Uri.EscapeDataString(part)}&langpair={Uri.EscapeDataString(source + "|" + target)}&mt=1";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"무료 번역 서비스 오류 ({(int)response.StatusCode})");

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var status = ReadResponseStatus(root);
            if (status != 200)
            {
                var details = root.TryGetProperty("responseDetails", out var detailsElement)
                    ? detailsElement.ToString()
                    : "번역 요청이 거부되었습니다.";
                throw new HttpRequestException($"무료 번역 서비스: {details}");
            }

            var translated = root.GetProperty("responseData").GetProperty("translatedText").GetString() ?? string.Empty;
            return WebUtility.HtmlDecode(translated);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static int ReadResponseStatus(JsonElement root)
    {
        if (!root.TryGetProperty("responseStatus", out var statusElement)) return 200;
        if (statusElement.ValueKind == JsonValueKind.Number && statusElement.TryGetInt32(out var numericStatus))
            return numericStatus;
        if (statusElement.ValueKind == JsonValueKind.String
            && int.TryParse(statusElement.GetString(), out var textStatus))
            return textStatus;

        // Some successful MyMemory responses omit the status or return it in an
        // unexpected JSON representation. The presence of responseData is a more
        // reliable success signal than failing the whole background translation.
        return root.TryGetProperty("responseData", out _) ? 200 : 500;
    }

    private static IReadOnlyList<string> SplitByUtf8Bytes(string text, int maximumBytes)
    {
        var parts = new List<string>();
        var builder = new StringBuilder();
        var byteCount = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.ToString();
            var runeBytes = Encoding.UTF8.GetByteCount(value);
            if (builder.Length > 0 && byteCount + runeBytes > maximumBytes)
            {
                parts.Add(builder.ToString());
                builder.Clear();
                byteCount = 0;
            }
            builder.Append(value);
            byteCount += runeBytes;
        }
        if (builder.Length > 0) parts.Add(builder.ToString());
        return parts;
    }

    private static string NormalizeLanguageCode(string languageTag)
    {
        var tag = languageTag.Trim();
        if (tag.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)) return "zh-TW";
        if (tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
        var separator = tag.IndexOfAny(['-', '_']);
        return (separator > 0 ? tag[..separator] : tag).ToLowerInvariant();
    }
}
