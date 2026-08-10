using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Agent.Tools
{
    public partial class AgentTools
    {
        private static readonly HttpClient InternetHttpClient = CreateInternetHttpClient();

        [Description("搜索互联网以获取最新公开信息。返回标题、来源域名、发布日期、链接与摘要。可按时间范围筛选，并优先返回指定来源的结果；需要详情时，再使用 FetchWebPage 读取具体页面正文。")]
        public static string SearchInternet(
            [Description("要搜索的关键词或问题")] string query,
            [Description("最多返回结果数，默认 5，范围 1 到 10")] int maximumResults = 5,
            [Description("时间范围：any、day、week、month 或 year，默认 any；仅对带发布日期的结果进行严格筛选")] string timeRange = "any",
            [Description("优先来源域名，例如 [\"openai.com\", \"learn.microsoft.com\"]；不指定时保留搜索引擎排序")] string[]? preferredDomains = null,
            [Description("检索语言：auto、zh-CN 或 en-US，默认 auto；auto 会根据关键词自动选择中英文检索区域")] string searchLanguage = "auto",
            [Description("网络区域：auto、china 或 global，默认 auto；中国大陆优先使用可访问的国内端点，并缩短海外来源超时")] string networkRegion = "auto")
        {
            return RunWithToolProgress("search_internet", $"正在搜索互联网：{query}", () =>
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    throw new ArgumentException("搜索关键词不能为空。", nameof(query));
                }

                maximumResults = Math.Clamp(maximumResults, 1, 10);
                var normalizedTimeRange = NormalizeTimeRange(timeRange);
                var normalizedDomains = NormalizeDomains(preferredDomains);
                var normalizedLanguage = NormalizeSearchLanguage(searchLanguage, query);
                var normalizedNetworkRegion = NormalizeNetworkRegion(networkRegion, normalizedLanguage);
                RequireApproval(
                "access_internet",
                $"搜索互联网：{query}",
                new Dictionary<string, string>
                {
                    ["query"] = query,
                    ["maximumResults"] = maximumResults.ToString(),
                    ["timeRange"] = normalizedTimeRange,
                    ["preferredDomains"] = string.Join(", ", normalizedDomains),
                    ["searchLanguage"] = normalizedLanguage,
                    ["networkRegion"] = normalizedNetworkRegion
                });

                var cutoff = GetTimeRangeCutoff(normalizedTimeRange);
                var attempts = SearchWithRegionalFallback(query, normalizedLanguage, normalizedNetworkRegion, maximumResults);
                var results = attempts.SelectMany(attempt => attempt.Results)
                .GroupBy(result => result.Link.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Where(result => cutoff is null || result.PublishedAt is null || result.PublishedAt >= cutoff)
                .OrderByDescending(result => IsPreferredDomain(result.Domain, normalizedDomains))
                .ThenByDescending(result => IsQueryMatch(result, query))
                .Take(maximumResults)
                .ToList();
                if (results.Count == 0)
                {
                    var failures = string.Join("；", attempts.Select(attempt => $"{attempt.Provider}：{attempt.Failure ?? "未返回结果"}"));
                    return $"未找到符合条件的互联网搜索结果。来源状态：{failures}";
                }

                var output = new StringBuilder();
                output.AppendLine($"搜索结果：{query}");
                output.AppendLine($"网络区域：{normalizedNetworkRegion}；检索语言：{normalizedLanguage}");
                output.AppendLine($"检索来源：{string.Join(" + ", attempts.Where(attempt => attempt.Results.Count > 0).Select(attempt => attempt.Provider))}");
                foreach (var failedAttempt in attempts.Where(attempt => attempt.Results.Count == 0 && !string.IsNullOrWhiteSpace(attempt.Failure)))
                {
                    output.AppendLine($"来源状态：{failedAttempt.Provider} {failedAttempt.Failure}");
                }
                if (cutoff is not null)
                {
                    output.AppendLine($"时间范围：{normalizedTimeRange}（无发布日期的结果会标记为未知）");
                }

                foreach (var result in results)
                {
                    output.AppendLine();
                    output.AppendLine($"标题：{result.Title}");
                    output.AppendLine($"来源：{result.Domain}");
                    output.AppendLine($"检索引擎：{result.SearchProvider}");
                    output.AppendLine($"发布日期：{result.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知"}");
                    output.AppendLine($"链接：{result.Link}");
                    if (!string.IsNullOrWhiteSpace(result.Description))
                    {
                        output.AppendLine($"摘要：{result.Description}");
                    }
                }

                return output.ToString();
            });
        }

        [Description("读取公开网页的正文内容。先通过 SearchInternet 找到可信链接，再用本工具读取页面详情；返回标题、来源域名、最终链接和经过清理的正文文本。")]
        public static string FetchWebPage(
            [Description("要读取的网页 http 或 https 链接")] string url,
            [Description("最多返回正文字符数，默认 12000，范围 1000 到 20000")] int maximumCharacters = 12000,
            [Description("请求超时时间，默认 8000 ms，范围 3000 到 15000 ms")] int timeoutMilliseconds = 8000)
        {
            return RunWithToolProgress("fetch_web_page", $"正在读取网页正文：{url}", () =>
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new ArgumentException("网页链接必须是有效的 http 或 https 地址。", nameof(url));
                }

                if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("不允许通过联网工具访问本机地址。", nameof(url));
                }

                maximumCharacters = Math.Clamp(maximumCharacters, 1_000, 20_000);
                timeoutMilliseconds = Math.Clamp(timeoutMilliseconds, 3_000, 15_000);
                RequireApproval(
                "access_internet",
                $"读取网页正文：{uri.Host}",
                new Dictionary<string, string>
                {
                    ["url"] = uri.ToString(),
                    ["maximumCharacters"] = maximumCharacters.ToString(),
                    ["timeoutMilliseconds"] = timeoutMilliseconds.ToString()
                });

                using var cancellation = new CancellationTokenSource(timeoutMilliseconds);
                HttpResponseMessage response;
                try
                {
                    response = InternetHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return $"读取网页超时（{timeoutMilliseconds} ms）：{uri.Host}。请使用搜索摘要或改用其他来源，不要重复请求此链接。";
                }

                using (response)
                {
                response.EnsureSuccessStatusCode();
                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (!string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase) && !string.Equals(contentType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                {
                    return $"无法读取网页正文：{uri.Host} 返回的内容类型为 {contentType ?? "未知"}，不是 HTML 页面。";
                }

                var html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var title = ExtractTitle(html);
                var content = ExtractReadableText(html, maximumCharacters);
                return $"标题：{title}\n来源：{uri.Host}\n链接：{response.RequestMessage?.RequestUri ?? uri}\n\n正文：\n{content}";
                }
            });
        }

        private static IReadOnlyList<SearchAttempt> SearchWithRegionalFallback(string query, string searchLanguage, string networkRegion, int maximumResults)
        {
            var attempts = new List<SearchAttempt>();
            if (networkRegion == "china")
            {
                attempts.Add(SearchBing(query, searchLanguage, "cn.bing.com", TimeSpan.FromSeconds(8), "Bing 中国"));
                if (attempts.Sum(attempt => attempt.Results.Count) < maximumResults)
                {
                    attempts.Add(SearchDuckDuckGo(query, searchLanguage, TimeSpan.FromSeconds(3)));
                }
            }
            else
            {
                attempts.Add(SearchDuckDuckGo(query, searchLanguage, TimeSpan.FromSeconds(8)));
                if (attempts.Sum(attempt => attempt.Results.Count) < maximumResults)
                {
                    attempts.Add(SearchBing(query, searchLanguage, "www.bing.com", TimeSpan.FromSeconds(8), "Bing"));
                }
            }

            return attempts;
        }

        private static SearchAttempt SearchBing(string query, string searchLanguage, string host, TimeSpan timeout, string provider)
        {
            try
            {
                using var cancellation = new CancellationTokenSource(timeout);
                var searchUri = new Uri($"https://{host}/search?format=rss&mkt={Uri.EscapeDataString(searchLanguage)}&setlang={Uri.EscapeDataString(searchLanguage)}&q={Uri.EscapeDataString(query)}");
                var response = InternetHttpClient.GetStringAsync(searchUri, cancellation.Token).GetAwaiter().GetResult();
                var document = XDocument.Parse(response);
                var results = document.Descendants("item")
                    .Select(item => CreateBingSearchResult(item, provider))
                    .Where(result => result is not null)
                    .Cast<SearchResult>()
                    .ToArray();
                return new SearchAttempt(provider, results, results.Length == 0 ? "未返回有效结果。" : null);
            }
            catch (OperationCanceledException)
            {
                return new SearchAttempt(provider, [], $"在 {timeout.TotalSeconds:0} 秒后超时，已跳过。");
            }
            catch (Exception exception) when (exception is HttpRequestException or System.Xml.XmlException)
            {
                return new SearchAttempt(provider, [], $"访问失败：{exception.Message}");
            }
        }

        private static SearchAttempt SearchDuckDuckGo(string query, string searchLanguage, TimeSpan timeout)
        {
            try
            {
                using var cancellation = new CancellationTokenSource(timeout);
                var region = searchLanguage.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ? "zh-cn" : "us-en";
                var searchUri = new Uri($"https://html.duckduckgo.com/html/?kl={region}&q={Uri.EscapeDataString(query)}");
                var html = InternetHttpClient.GetStringAsync(searchUri, cancellation.Token).GetAwaiter().GetResult();
                var matches = Regex.Matches(html, "<a\\b(?=[^>]*\\bclass\\s*=\\s*[\\\"'][^\\\"']*\\bresult__a\\b)(?<attributes>[^>]*)>(?<title>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var results = new List<SearchResult>();
                foreach (Match match in matches)
                {
                    var href = ExtractHtmlAttribute(match.Groups["attributes"].Value, "href");
                    var uri = ResolveDuckDuckGoResultUri(href);
                    var title = CleanHtml(match.Groups["title"].Value);
                    if (uri is null || string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    var followingHtmlLength = Math.Min(3_000, html.Length - (match.Index + match.Length));
                    var followingHtml = html.Substring(match.Index + match.Length, followingHtmlLength);
                    var snippetMatch = Regex.Match(followingHtml, "<[^>]*class\\s*=\\s*[\\\"'][^\\\"']*\\bresult__snippet\\b[^\\\"']*[\\\"'][^>]*>(.*?)</[^>]+>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    var description = snippetMatch.Success ? CleanHtml(snippetMatch.Groups[1].Value) : string.Empty;
                    results.Add(new SearchResult(title, uri, uri.Host, description, null, "DuckDuckGo"));
                }

                return new SearchAttempt("DuckDuckGo", results, results.Count == 0 ? "未返回有效结果。" : null);
            }
            catch (OperationCanceledException)
            {
                return new SearchAttempt("DuckDuckGo", [], $"在 {timeout.TotalSeconds:0} 秒后超时，已跳过。");
            }
            catch (HttpRequestException exception)
            {
                return new SearchAttempt("DuckDuckGo", [], $"访问失败：{exception.Message}");
            }
        }

        private static SearchResult? CreateBingSearchResult(XElement item, string provider)
        {
            var title = CleanHtml(item.Element("title")?.Value);
            var link = item.Element("link")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(title) || !Uri.TryCreate(link, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var dateText = item.Element("pubDate")?.Value;
            DateTimeOffset? publishedAt = DateTimeOffset.TryParse(dateText, out var date) ? date : null;
            return new SearchResult(title, uri, uri.Host, CleanHtml(item.Element("description")?.Value), publishedAt, provider);
        }

        private static string NormalizeSearchLanguage(string searchLanguage, string query)
        {
            var value = searchLanguage.Trim();
            if (value.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) || value.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (!value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("检索语言仅支持 auto、zh-CN 或 en-US。", nameof(searchLanguage));
            }

            return query.Any(character => character is >= '\u4e00' and <= '\u9fff') ? "zh-CN" : "en-US";
        }

        private static string NormalizeNetworkRegion(string networkRegion, string searchLanguage)
        {
            var value = networkRegion.Trim().ToLowerInvariant();
            if (value is "china" or "global")
            {
                return value;
            }

            if (value != "auto")
            {
                throw new ArgumentException("网络区域仅支持 auto、china 或 global。", nameof(networkRegion));
            }

            try
            {
                return RegionInfo.CurrentRegion.TwoLetterISORegionName.Equals("CN", StringComparison.OrdinalIgnoreCase)
                    || searchLanguage.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
                    ? "china"
                    : "global";
            }
            catch (ArgumentException)
            {
                return CultureInfo.CurrentCulture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
                    || searchLanguage.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
                    ? "china"
                    : "global";
            }
        }

        private static string? ExtractHtmlAttribute(string attributes, string attributeName)
        {
            var match = Regex.Match(attributes, $"\\b{Regex.Escape(attributeName)}\\s*=\\s*[\\\"'](?<value>.*?)[\\\"']", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : null;
        }

        private static Uri? ResolveDuckDuckGoResultUri(string? href)
        {
            if (string.IsNullOrWhiteSpace(href))
            {
                return null;
            }

            var absoluteUri = Uri.TryCreate(href, UriKind.Absolute, out var directUri)
                ? directUri
                : new Uri(new Uri("https://duckduckgo.com"), href);
            var redirectTarget = absoluteUri.Query.TrimStart('?').Split('&')
                .Select(pair => pair.Split('=', 2))
                .FirstOrDefault(pair => pair.Length == 2 && pair[0].Equals("uddg", StringComparison.OrdinalIgnoreCase));
            var resultUrl = redirectTarget is null ? absoluteUri.AbsoluteUri : Uri.UnescapeDataString(redirectTarget[1].Replace('+', ' '));
            return Uri.TryCreate(resultUrl, UriKind.Absolute, out var resultUri) && (resultUri.Scheme == Uri.UriSchemeHttp || resultUri.Scheme == Uri.UriSchemeHttps)
                ? resultUri
                : null;
        }

        private static string NormalizeTimeRange(string timeRange)
        {
            var value = timeRange.Trim().ToLowerInvariant();
            return value is "any" or "day" or "week" or "month" or "year"
                ? value
                : throw new ArgumentException("时间范围仅支持 any、day、week、month 或 year。", nameof(timeRange));
        }

        private static DateTimeOffset? GetTimeRangeCutoff(string timeRange) => timeRange switch
        {
            "day" => DateTimeOffset.UtcNow.AddDays(-1),
            "week" => DateTimeOffset.UtcNow.AddDays(-7),
            "month" => DateTimeOffset.UtcNow.AddMonths(-1),
            "year" => DateTimeOffset.UtcNow.AddYears(-1),
            _ => null
        };

        private static string[] NormalizeDomains(string[]? domains) => (domains ?? [])
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Select(domain => domain.Trim().TrimStart('.').ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();

        private static bool IsPreferredDomain(string domain, string[] preferredDomains) => preferredDomains.Any(preferred =>
            domain.Equals(preferred, StringComparison.OrdinalIgnoreCase) || domain.EndsWith($".{preferred}", StringComparison.OrdinalIgnoreCase));

        private static bool IsQueryMatch(SearchResult result, string query) =>
            result.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || result.Description.Contains(query, StringComparison.OrdinalIgnoreCase);

        private static string ExtractTitle(string html)
        {
            var match = Regex.Match(html, "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? CleanHtml(match.Groups[1].Value) : "未提供标题";
        }

        private static string ExtractReadableText(string html, int maximumCharacters)
        {
            var withoutNonContent = Regex.Replace(html, "<(script|style|noscript|svg|nav|footer|header)[^>]*>.*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var text = CleanHtml(withoutNonContent);
            if (string.IsNullOrWhiteSpace(text))
            {
                return "页面未包含可提取的文本内容。";
            }

            return text.Length <= maximumCharacters ? text : $"{text[..maximumCharacters]}\n\n[正文已按长度截断]";
        }

        private static string CleanHtml(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var withoutTags = Regex.Replace(value, "<[^>]+>", " ");
            return Regex.Replace(WebUtility.HtmlDecode(withoutTags), "\\s+", " ").Trim();
        }

        private static HttpClient CreateInternetHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("QianAgent/1.0");
            return client;
        }

        private sealed record SearchAttempt(string Provider, IReadOnlyList<SearchResult> Results, string? Failure);

        private sealed record SearchResult(string Title, Uri Link, string Domain, string Description, DateTimeOffset? PublishedAt, string SearchProvider);
    }
}
