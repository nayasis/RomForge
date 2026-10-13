using HtmlAgilityPack;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace RomForge.Core.Services.Util;

public static class TistoryAttachmentService
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const string TargetDivXPath = "//div[contains(@class, 'tt_article_useless_p_margin') and contains(@class, 'contents_style')]";
    private const int TimeoutSeconds = 30;

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, string> FileNameCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> FileSizeCache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<List<string>> ExtractAttachmentUrlsAsync(IEnumerable<string> pageUrls, Action<string>? log = null, CancellationToken ct = default)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pageUrl in pageUrls)
        {
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                continue;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);

                request.Headers.Add("User-Agent", UserAgent);

                using var response = await Http.SendAsync(request, cts.Token);

                response.EnsureSuccessStatusCode();

                var html = await response.Content.ReadAsStringAsync(cts.Token);
                var doc = new HtmlDocument();

                doc.LoadHtml(html);

                var targetNode = doc.DocumentNode.SelectSingleNode(TargetDivXPath);
                var searchNode = targetNode ?? doc.DocumentNode;

                foreach (var node in searchNode.DescendantsAndSelf())
                {
                    if (node.Name == "a")
                    {
                        var href = node.GetAttributeValue("href", string.Empty).Trim();

                        if (IsCandidateUrl(href) && IsAttachmentUrl(href))
                        {
                            urls.Add(href);

                            var text = HtmlEntity.DeEntitize(node.InnerText).Trim();
                            var containerText = node.ParentNode != null ? HtmlEntity.DeEntitize(node.ParentNode.InnerText).Trim() : text;
                            var sizeMatch = Regex.Match(containerText, @"([\d.]+)\s*(KB|MB|GB)", RegexOptions.IgnoreCase);

                            if (!sizeMatch.Success)
                                sizeMatch = Regex.Match(text, @"([\d.]+)\s*(KB|MB|GB)", RegexOptions.IgnoreCase);

                            if (sizeMatch.Success)
                            {
                                var targetSourceText = containerText.Contains(sizeMatch.Value) ? containerText : text;
                                var rawName = targetSourceText[..sizeMatch.Index].Trim();

                                rawName = Regex.Replace(rawName, @"[\-\|]+$", "").Trim();
                                rawName = rawName.Trim('[', ']', '(', ')', '{', '}').Trim();

                                if (!string.IsNullOrWhiteSpace(rawName) && !rawName.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                    FileNameCache[href] = rawName;

                                if (double.TryParse(sizeMatch.Groups[1].Value, out double num))
                                {
                                    string unit = sizeMatch.Groups[2].Value.ToUpper();
                                    long bytes = unit switch
                                    {
                                        "KB" => (long)(num * 1024),
                                        "MB" => (long)(num * 1024 * 1024),
                                        "GB" => (long)(num * 1024 * 1024 * 1024),
                                        _ => -1
                                    };

                                    if (bytes > 0)
                                        FileSizeCache[href] = bytes;
                                }
                            }
                            else
                            {
                                var cleaned = Regex.Replace(text, @"\s*[\(\[\{]\s*[\d\.]+\s*(KB|MB|GB|Bytes|bytes)?\s*[\)\]\}]", "", RegexOptions.IgnoreCase);
                                cleaned = cleaned.Replace("다운로드", "").Trim();

                                if (!string.IsNullOrWhiteSpace(cleaned) && !cleaned.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                    FileNameCache[href] = cleaned;
                                else
                                {
                                    var title = node.GetAttributeValue("title", string.Empty).Trim();

                                    if (!string.IsNullOrWhiteSpace(title))
                                        FileNameCache[href] = title;
                                }
                            }
                        }
                    }

                    var dataUrl = node.GetAttributeValue("data-url", string.Empty).Trim();

                    if (IsCandidateUrl(dataUrl) && IsAttachmentUrl(dataUrl))
                        urls.Add(dataUrl);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"URL 처리 실패 ({pageUrl}): {ex.Message}");
            }
        }

        var filtered = urls
            .OrderByDescending(GetUrlPriority)
            .ToList();

        return filtered;
    }

    public static string GetCachedFileName(string url)
    {
        if (FileNameCache.TryGetValue(url, out var name) && !string.IsNullOrWhiteSpace(name))
            return name;

        int fnameIdx = url.IndexOf("fname=", StringComparison.OrdinalIgnoreCase);

        if (fnameIdx >= 0)
        {
            string fnameVal = url[(fnameIdx + 6)..];
            int ampersandIdx = fnameVal.IndexOf('&');

            if (ampersandIdx >= 0)
                fnameVal = fnameVal[..ampersandIdx];

            fnameVal = Uri.UnescapeDataString(fnameVal);

            if (!string.IsNullOrWhiteSpace(fnameVal))
                return fnameVal;
        }

        var withoutQuery = url.Split('?', '#')[0];

        return Path.GetFileName(withoutQuery);
    }

    public static long GetCachedFileSize(string url)
    {
        if (FileSizeCache.TryGetValue(url, out var size))
            return size;

        return -1;
    }

    private static readonly HashSet<string> AttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".alz", ".egg", ".gz", ".tar",
        ".iso", ".bin", ".cue", ".img", ".chd", ".gdi",
        ".nsp", ".xci", ".nsz", ".xcz", ".3ds", ".cia", ".cci",
        ".gba", ".gbc", ".gb", ".nes", ".sfc", ".smc", ".n64", ".z64", ".pce", ".md", ".sms"
    };

    private static bool IsAttachmentUrl(string url)
    {
        var withoutQuery = url.Split('?', '#')[0];
        var ext = Path.GetExtension(withoutQuery);

        if (url.Contains("attachment"))
            return true;

        if (url.Contains("kakaocdn.net") && !url.EndsWith(".html") && !url.EndsWith('/'))
            return true;

        if ((url.Contains("daum.net") || url.Contains("daumcdn.net")) &&
            (url.Contains("cfile") || url.Contains("attach") || url.Contains("original") || url.Contains("tistoryfile") || AttachmentExtensions.Contains(ext)))
            return true;

        if (AttachmentExtensions.Contains(ext))
            return true;

        return false;
    }

    public static async Task<(string SavedPath, long FileSizeBytes)> DownloadAsync(string fileUrl, string saveDir, string preferredFileName, IProgress<int>? progress, string? partialPath = null, bool overwriteExisting = false, Action<string, string>? onPathsResolved = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(saveDir);

        if (string.IsNullOrWhiteSpace(preferredFileName) || preferredFileName.Length > 50 || !Path.HasExtension(preferredFileName))
        {
            var cached = GetCachedFileName(fileUrl);

            if (!string.IsNullOrWhiteSpace(cached))
                preferredFileName = cached;
        }

        bool canResume = !string.IsNullOrWhiteSpace(partialPath) && File.Exists(partialPath);
        long existingBytes = canResume ? new FileInfo(partialPath!).Length : 0;
        string? filePath = canResume ? partialPath![..^".download".Length] : null;
        string? temporaryPath = canResume ? partialPath : null;

        using var request = new HttpRequestMessage(HttpMethod.Get, fileUrl);

        request.Headers.Add("User-Agent", UserAgent);

        if (canResume && existingBytes > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        response.EnsureSuccessStatusCode();

        if (!canResume)
        {
            var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"');

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = preferredFileName;

            fileName = SanitizeFileName(fileName);
            filePath = Path.Combine(saveDir, fileName);

            if (!overwriteExisting)
                filePath = GetUniquePath(filePath);

            temporaryPath = filePath + ".download";
            onPathsResolved?.Invoke(filePath, temporaryPath);
        }

        bool append = canResume && response.StatusCode == System.Net.HttpStatusCode.PartialContent;

        if (!append)
            existingBytes = 0;

        var totalBytes = response.Content.Headers.ContentLength is long contentLength
            ? contentLength + existingBytes
            : -1;
        long readTotal = 0;

        try
        {
            await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(temporaryPath!, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            int read;

            while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);

                readTotal += read;

                if (totalBytes > 0)
                    progress?.Report((int)((readTotal + existingBytes) * 100 / totalBytes));
            }

            if (totalBytes > 0 && readTotal + existingBytes != totalBytes)
                throw new EndOfStreamException($"다운로드가 완료되기 전에 연결이 종료되었습니다. (수신: {readTotal + existingBytes:N0} / 예상: {totalBytes:N0} bytes)");

            await fileStream.FlushAsync(ct);
            await fileStream.DisposeAsync();
            File.Move(temporaryPath!, filePath!, overwriteExisting);
        }
        catch
        {
            throw;
        }

        progress?.Report(100);

        return (filePath!, readTotal + existingBytes);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true };

        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
    }

    private static bool IsCandidateUrl(string url) => !string.IsNullOrWhiteSpace(url) && url != "#" && !url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase);

    private static int GetUrlPriority(string url)
    {
        if (url.Contains("kakaocdn.net"))
            return 3;

        if (url.Contains("daum.net") || url.Contains("daumcdn.net"))
            return 2;

        if (url.Contains("tistory"))
            return 1;

        return 0;
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(c, '_');

        return string.IsNullOrWhiteSpace(fileName) ? $"file_{Guid.NewGuid().ToString()[..8]}" : fileName;
    }

    private static string GetUniquePath(string filePath)
    {
        if (!File.Exists(filePath))
            return filePath;

        var dir = Path.GetDirectoryName(filePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);
        var index = 1;
        string candidate;

        do
        {
            candidate = Path.Combine(dir, $"{name} ({index}){ext}");
            index++;
        }
        while (File.Exists(candidate));

        return candidate;
    }
}
