using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace CustomImageViewer.Services;

public sealed class BuiltInQwenTranslatorService : IDisposable
{
    public const string ProviderId = "BuiltInQwen";
    public const string CacheProviderId = "BuiltInQwen8B-DirectMeaningV2";
    public const string ModelDisplayName = "Qwen3-8B Q4_K_M";
    private const string ModelFileName = "Qwen3-8B-Q4_K_M.gguf";
    private const string ModelUrl =
        "https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q4_K_M.gguf?download=true";

    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private LLamaWeights? _weights;
    private ModelParams? _modelParams;
    private string _modelFolder = DefaultModelFolder;

    public static string DefaultModelFolder => Path.Combine(
        AppContext.BaseDirectory,
        "TranslationModel");

    public string ModelFolder => _modelFolder;
    public string ModelPath => Path.Combine(ModelFolder, ModelFileName);
    private string LegacyModelPath => Path.Combine(ModelFolder, "Qwen3-4B-Q4_K_M.gguf");
    public bool IsModelInstalled => File.Exists(ModelPath) && new FileInfo(ModelPath).Length > 4_000_000_000;

    public static string GetModelPath(string folder) =>
        Path.Combine(Path.GetFullPath(folder), ModelFileName);

    public static bool IsModelInstalledIn(string folder)
    {
        var path = GetModelPath(folder);
        return File.Exists(path) && new FileInfo(path).Length > 4_000_000_000;
    }

    public async Task ConfigureModelFolderAsync(
        string? folder,
        CancellationToken cancellationToken = default)
    {
        var configured = string.IsNullOrWhiteSpace(folder) ? DefaultModelFolder : Path.GetFullPath(folder.Trim());
        if (string.Equals(configured, _modelFolder, StringComparison.OrdinalIgnoreCase)) return;
        await _inferenceGate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(configured, _modelFolder, StringComparison.OrdinalIgnoreCase)) return;
            _weights?.Dispose();
            _weights = null;
            _modelParams = null;
            _modelFolder = configured;
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    public async Task MoveInstalledModelAsync(
        string destinationFolder,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(destinationFolder.Trim());
        if (string.Equals(destination, _modelFolder, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(1);
            return;
        }
        if (!IsModelInstalled)
        {
            await ConfigureModelFolderAsync(destination, cancellationToken);
            progress?.Report(1);
            return;
        }

        await _downloadGate.WaitAsync(cancellationToken);
        var inferenceGateAcquired = false;
        string? temporaryPath = null;
        var sourcePath = ModelPath;
        try
        {
            await _inferenceGate.WaitAsync(cancellationToken);
            inferenceGateAcquired = true;
            _weights?.Dispose();
            _weights = null;
            _modelParams = null;
            Directory.CreateDirectory(destination);
            var destinationPath = GetModelPath(destination);
            if (IsModelInstalledIn(destination))
            {
                _modelFolder = destination;
                progress?.Report(1);
                return;
            }

            if (string.Equals(
                    Path.GetPathRoot(sourcePath),
                    Path.GetPathRoot(destinationPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
            }
            else
            {
                temporaryPath = destinationPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.moving";
                var sourceLength = new FileInfo(sourcePath).Length;
                await using (var source = new FileStream(
                    sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var target = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[4 * 1024 * 1024];
                    long copied = 0;
                    while (true)
                    {
                        var count = await source.ReadAsync(buffer, cancellationToken);
                        if (count == 0) break;
                        await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                        copied += count;
                        progress?.Report((double)copied / sourceLength);
                    }
                    await target.FlushAsync(cancellationToken);
                }

                if (new FileInfo(temporaryPath).Length != sourceLength)
                    throw new IOException("모델 파일 복사 크기가 원본과 일치하지 않습니다.");
                File.Move(temporaryPath, destinationPath, overwrite: true);
                temporaryPath = null;
                File.Delete(sourcePath);
            }

            _modelFolder = destination;
            progress?.Report(1);
        }
        catch
        {
            try
            {
                if (temporaryPath is not null && File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch { }
            throw;
        }
        finally
        {
            if (inferenceGateAcquired) _inferenceGate.Release();
            _downloadGate.Release();
        }
    }

    public async Task DownloadModelAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _downloadGate.WaitAsync(cancellationToken);
        string? temporaryPath = null;
        try
        {
            if (IsModelInstalled)
            {
                progress?.Report(1);
                return;
            }

            Directory.CreateDirectory(ModelFolder);
            temporaryPath = ModelPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.download";
            using var response = await _httpClient.GetAsync(
                ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);

            // Windows에서는 열린 FileStream을 Move할 수 없으므로 다운로드 스트림을
            // 이 블록 안에서 완전히 닫은 뒤 최종 모델 파일명으로 교체한다.
            await using (var destination = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];
                long received = 0;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, cancellationToken);
                    if (count == 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    received += count;
                    if (totalBytes > 0) progress?.Report((double)received / totalBytes.Value);
                }
                await destination.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, ModelPath, overwrite: true);
            temporaryPath = null;
            try
            {
                if (File.Exists(LegacyModelPath)) File.Delete(LegacyModelPath);
                var defaultLegacyPath = Path.Combine(DefaultModelFolder, "Qwen3-4B-Q4_K_M.gguf");
                if (!string.Equals(defaultLegacyPath, LegacyModelPath, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(defaultLegacyPath))
                    File.Delete(defaultLegacyPath);
            }
            catch
            {
                // A running older process may still hold the 4B model. It can be
                // removed on a later download or by the user without affecting 8B.
            }
            progress?.Report(1);
        }
        catch
        {
            try
            {
                if (temporaryPath is not null && File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch { }
            throw;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    public async Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (!IsModelInstalled)
            throw new FileNotFoundException(
                "고품질 로컬 번역 모델이 설치되지 않았습니다. 설정 > 뷰어 및 번역에서 모델을 다운로드하세요.",
                ModelPath);

        var sourceName = GetLanguageName(sourceLanguage);
        var targetName = GetLanguageName(targetLanguage);
        var prompt =
            $"Translate the following complete text from {sourceName} to {targetName}.\n" +
            "Return only the translation. Do not summarize, omit, or explain anything.\n\n" + text;
        return await GenerateAsync(
            prompt, sourceName, targetName, cancellationToken,
            Math.Clamp(text.Length * 2 + 128, 128, 2048));
    }

    public async Task<IReadOnlyList<string>> TranslateBlocksAsync(
        IReadOnlyList<string> blocks,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (blocks.Count == 0) return [];
        if (!IsModelInstalled)
            throw new FileNotFoundException(
                "고품질 로컬 번역 모델이 설치되지 않았습니다. 설정 > 뷰어 및 번역에서 모델을 다운로드하세요.",
                ModelPath);

        var sourceName = GetLanguageName(sourceLanguage);
        var targetName = GetLanguageName(targetLanguage);
        // Translate the OCR source directly. A separate LLM proofreading pass was
        // both slow and could turn Japanese into phonetic Korean before translation.
        var sourceBlocks = blocks.Select(block => block?.Trim() ?? string.Empty).ToList();
        var activeIndexes = Enumerable.Range(0, sourceBlocks.Count)
            .Where(index => ContainsTranslatableText(sourceBlocks[index]))
            .ToList();
        var finalResult = Enumerable.Repeat(string.Empty, blocks.Count).ToArray();
        if (activeIndexes.Count == 0) return finalResult;

        var input = new StringBuilder();
        foreach (var index in activeIndexes)
            input.AppendLine($"[BLOCK_{index:D4}] {sourceBlocks[index].Replace("\r", " ").Replace("\n", " ")}");

        var semanticExample = sourceName == "Japanese" && targetName == "Korean"
            ? "Semantic translation example: [BLOCK_0000] すごい力なんて / [BLOCK_0001] こんな力あったっけ " +
              "means [BLOCK_0000] 엄청난 힘이라니 / [BLOCK_0001] 이런 힘이 있었던가? " +
              "It must not be written as Japanese pronunciation in Hangul.\n"
            : string.Empty;
        var prompt =
            $"Translate every numbered OCR block from {sourceName} to {targetName}. " +
            "The blocks come from one manga or document page, so use all blocks as context.\n" +
            "The OCR may contain minor look-alike character mistakes. Infer the intended natural source phrase " +
            "from context before translating, without mentioning or reproducing the OCR error.\n" +
            "Translate the meaning, not the pronunciation. Never transliterate source-language sounds into the " +
            "target alphabet. For Japanese-to-Korean translation, write natural Korean meaning rather than " +
            "Japanese readings in Hangul.\n" +
            semanticExample +
            "Output exactly one entry for every input block, in the same order and using the same marker. " +
            "Never merge, skip, summarize, or renumber blocks. Do not repeat the source text.\n" +
            "Required format: [BLOCK_0000] translated text\n\n" + input;

        var outputTokenLimit = Math.Clamp(
            activeIndexes.Sum(index => sourceBlocks[index].Length) * 2 + 128,
            128,
            2048);
        var raw = await GenerateAsync(
            prompt, sourceName, targetName, cancellationToken, outputTokenLimit);
        var translated = ParseNumberedBlocks(raw, blocks.Count);

        // Retry all omitted markers together. This avoids a slow model invocation for
        // every missing fragment while still preventing blank overlay regions.
        var missingIndexes = activeIndexes
            .Where(index => string.IsNullOrWhiteSpace(translated[index]))
            .ToList();
        if (missingIndexes.Count > 0)
        {
            var retryInput = new StringBuilder();
            foreach (var index in missingIndexes)
                retryInput.AppendLine($"[BLOCK_{index:D4}] {sourceBlocks[index].Replace("\r", " ").Replace("\n", " ")}");
            var retryPrompt =
                $"Translate the meaning of every numbered block from {sourceName} to {targetName}. " +
                "Do not transliterate pronunciation, omit a block, or repeat the source. " +
                "Return the same markers followed by natural target-language translations only.\n\n" + retryInput;
            var retryRaw = await GenerateAsync(
                retryPrompt,
                sourceName,
                targetName,
                cancellationToken,
                Math.Clamp(missingIndexes.Sum(index => sourceBlocks[index].Length) * 2 + 96, 96, 1024));
            var retryResult = ParseNumberedBlocks(retryRaw, blocks.Count);
            foreach (var index in missingIndexes)
                if (!string.IsNullOrWhiteSpace(retryResult[index]))
                    translated[index] = retryResult[index];
        }

        foreach (var index in activeIndexes)
            finalResult[index] = translated[index];
        return finalResult;
    }

    private static bool ContainsTranslatableText(string value) =>
        value.EnumerateRunes().Any(Rune.IsLetter);

    private async Task<string> GenerateAsync(
        string prompt,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken,
        int? maxTokens = null)
    {
        await _inferenceGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureModelLoadedAsync(cancellationToken);
            var executor = new StatelessExecutor(_weights!, _modelParams!)
            {
                ApplyTemplate = true,
                SystemMessage =
                    "You are a professional manga and document translator. Translate the complete source text " +
                    $"from {sourceLanguage} into {targetLanguage}. Use the surrounding lines as context. " +
                    "Preserve names, numbers, punctuation, markers, paragraph order, and line breaks. " +
                    "Silently correct obvious OCR character mistakes from context. " +
                    "Translate semantic meaning; never transliterate pronunciation into the target script. " +
                    "Never summarize or omit text. Use natural conversational language. " +
                    "Return only the requested translation without explanations. /no_think"
            };
            var inference = new InferenceParams
            {
                MaxTokens = maxTokens ?? Math.Clamp(prompt.Length * 2, 128, 2048),
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.1f
                }
            };
            var output = new StringBuilder();
            await foreach (var piece in executor.InferAsync(prompt + "\n/no_think", inference, cancellationToken))
                output.Append(piece);
            return RemoveThinking(output.ToString()).Trim();
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private static string[] ParseNumberedBlocks(string value, int count)
    {
        // A local model can omit one or more requested markers. Keep every slot
        // non-null so callers can detect a missing block as an empty string and
        // retry it or fall back to the original OCR text safely.
        var result = Enumerable.Repeat(string.Empty, count).ToArray();
        const string marker = @"(?:\*\*)?\[?BLOCK[_\s-]?(\d{1,4})\]?(?:\*\*)?\s*(?:[:：-]\s*)?";
        var matches = Regex.Matches(
            value,
            $@"(?ms){marker}(.*?)(?=\s*{marker}|\z)");
        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups[1].Value, out var index) || index < 0 || index >= count)
                continue;
            result[index] = match.Groups[2].Value.Trim();
        }
        return result;
    }

    private static string GetLanguageName(string language) => language.ToLowerInvariant() switch
    {
        "ja" or "japanese" or "일본어" => "Japanese",
        "zh" or "zh-hans" or "zh-hant" or "chinese" or "중국어" => "Chinese",
        "ko" or "korean" or "한국어" => "Korean",
        "en" or "english" or "영어" => "English",
        _ => language
    };

    private async Task EnsureModelLoadedAsync(CancellationToken cancellationToken)
    {
        if (_weights is not null) return;
        cancellationToken.ThrowIfCancellationRequested();
        var modelParams = new ModelParams(ModelPath)
        {
            ContextSize = 4096,
            Threads = Math.Max(2, Environment.ProcessorCount - 1),
            BatchSize = 512,
            GpuLayerCount = 0
        };

        // Page navigation cancels the translation request. Cancelling llama.cpp while
        // it is mapping/repacking a multi-gigabyte model can leave the native loader in
        // an invalid state. Finish this one-time load, cache it, then honor the request
        // cancellation before inference starts.
        var weights = await LLamaWeights.LoadFromFileAsync(modelParams, CancellationToken.None);
        _modelParams = modelParams;
        _weights = weights;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string RemoveThinking(string value)
    {
        var end = value.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        return end >= 0 ? value[(end + "</think>".Length)..] : value;
    }

    public void Dispose()
    {
        _weights?.Dispose();
        _httpClient.Dispose();
        _downloadGate.Dispose();
        _inferenceGate.Dispose();
    }
}
