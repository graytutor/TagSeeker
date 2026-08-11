using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CustomImageViewer.Services;

public sealed class MyMemoryTranslatorService
{
    private const int MaximumQueryBytes = 480;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var source = NormalizeLanguageCode(sourceLanguage);
        var target = NormalizeLanguageCode(targetLanguage);
        var translatedLines = new List<string>();

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                translatedLines.Add(string.Empty);
                continue;
            }

            var translatedParts = new List<string>();
            foreach (var part in SplitByUtf8Bytes(line, MaximumQueryBytes))
            {
                var url = "https://api.mymemory.translated.net/get" +
                          $"?q={Uri.EscapeDataString(part)}&langpair={Uri.EscapeDataString(source + "|" + target)}&mt=1";
                using var response = await _httpClient.GetAsync(url, cancellationToken);
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"무료 번역 서비스 오류 ({(int)response.StatusCode})");

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var status = root.TryGetProperty("responseStatus", out var statusElement) ? statusElement.GetInt32() : 200;
                if (status != 200)
                {
                    var details = root.TryGetProperty("responseDetails", out var detailsElement)
                        ? detailsElement.ToString()
                        : "번역 요청이 거부되었습니다.";
                    throw new HttpRequestException($"무료 번역 서비스: {details}");
                }

                var translated = root.GetProperty("responseData").GetProperty("translatedText").GetString() ?? string.Empty;
                translatedParts.Add(WebUtility.HtmlDecode(translated));
            }
            translatedLines.Add(string.Concat(translatedParts));
        }

        return string.Join(Environment.NewLine, translatedLines);
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
