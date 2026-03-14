using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LuminChatWin.Core.Services;

public sealed class WebToolClient
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36 lumin-chat-win/1.0";

    public async Task<Dictionary<string, object?>> FetchPageAsync(string url, int maxChars = 12000, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var title = Regex.Match(html, "<title[^>]*>(?<title>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups["title"].Value.Trim();
        var text = StripHtml(html);
        return new Dictionary<string, object?>
        {
            ["url"] = url,
            ["final_url"] = response.RequestMessage?.RequestUri?.ToString() ?? url,
            ["status_code"] = (int)response.StatusCode,
            ["content_type"] = response.Content.Headers.ContentType?.ToString() ?? string.Empty,
            ["title"] = System.Net.WebUtility.HtmlDecode(title),
            ["text"] = text[..Math.Min(maxChars, text.Length)],
            ["truncated"] = text.Length > maxChars,
        };
    }

    public async Task<Dictionary<string, object?>> SearchAsync(string query, int limit = 5, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync($"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var matches = Regex.Matches(body, "<a[^>]+class=\"result__a\"[^>]+href=\"(?<href>[^\"]+)\"[^>]*>(?<title>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var snippetMatches = Regex.Matches(body, "<a[^>]+class=\"result__snippet\"[^>]*>(?<snippet>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var results = new List<Dictionary<string, string>>();
        for (var index = 0; index < matches.Count && results.Count < limit; index++)
        {
            var snippet = index < snippetMatches.Count ? CleanHtml(snippetMatches[index].Groups["snippet"].Value) : string.Empty;
            results.Add(new Dictionary<string, string>
            {
                ["title"] = CleanHtml(matches[index].Groups["title"].Value),
                ["url"] = System.Net.WebUtility.HtmlDecode(matches[index].Groups["href"].Value),
                ["snippet"] = snippet,
            });
        }

        return new Dictionary<string, object?>
        {
            ["query"] = query,
            ["engine"] = "duckduckgo-html",
            ["count"] = results.Count,
            ["results"] = results,
        };
    }

    public static string FormatPayload(Dictionary<string, object?> payload) => JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("zh-CN"));
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en", 0.8));
        return client;
    }

    private static string StripHtml(string html)
    {
        var noScript = Regex.Replace(html, "<(script|style|noscript)[^>]*>.*?</\\1>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var withBreaks = Regex.Replace(noScript, "</?(p|div|section|article|li|h1|h2|h3|br)[^>]*>", "\n", RegexOptions.IgnoreCase);
        var noTags = Regex.Replace(withBreaks, "<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        var clean = Regex.Replace(decoded, "[ \t]{2,}", " ");
        return Regex.Replace(clean, "\n{3,}", "\n\n").Trim();
    }

    private static string CleanHtml(string raw)
    {
        return Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(raw, "<[^>]+>", " ")), "\\s+", " ").Trim();
    }
}