using System.Net;
using System.Text.RegularExpressions;
using System.Xml;

namespace Chronicle.Plugin.Letterboxd;

/// <summary>
/// Fetches and parses Letterboxd RSS feeds.
///
/// Letterboxd does not have a public API. All data is read from the RSS feeds
/// they provide for each user's diary, watchlist, and ratings.
///
/// Feed URLs:
///   Diary     https://letterboxd.com/{username}/rss/
///   Watchlist https://letterboxd.com/{username}/watchlist/rss/
///   Ratings   https://letterboxd.com/{username}/films/ratings/rss/
///
/// Letterboxd does not publish rate limits for RSS. The plugin uses a polite
/// 1-second delay between pages and respects 429 Retry-After headers.
///
/// Pagination: RSS feeds are limited to 50 items per page. Use ?page=N for more.
/// </summary>
internal sealed class LetterboxdRssClient : IDisposable
{
    private const string BaseUrl      = "https://letterboxd.com";
    private const string LbxdNs      = "https://letterboxd.com";
    private const string TmdbNs      = "https://themoviedb.org";

    // Letterboxd RSS uses these custom namespaces
    private const string LbxdPrefix  = "letterboxd";
    private const string TmdbPrefix  = "tmdb";

    // Politeness delay between paginated requests (ms)
    private const int PolitenessDelayMs = 1000;

    private readonly HttpClient _http;

    internal LetterboxdRssClient()
    {
        _http = new HttpClient();
        // Letterboxd requires a descriptive User-Agent
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Chronicle/1.0 (https://github.com/thegoddamnbeckster/Chronicle)");
    }

    // ── Feed fetchers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches all diary entries (watched films) for the given Letterboxd username.
    /// Paginates automatically until all entries are retrieved.
    /// </summary>
    internal async Task<List<LetterboxdEntry>> GetDiaryAsync(
        string username, DateTimeOffset? since, CancellationToken ct)
    {
        return await FetchAllPagesAsync(
            $"{BaseUrl}/{username}/rss/",
            LetterboxdFeedType.Diary,
            since, ct);
    }

    /// <summary>Fetches the user's watchlist.</summary>
    internal async Task<List<LetterboxdEntry>> GetWatchlistAsync(
        string username, CancellationToken ct)
    {
        return await FetchAllPagesAsync(
            $"{BaseUrl}/{username}/watchlist/rss/",
            LetterboxdFeedType.Watchlist,
            null, ct);
    }

    /// <summary>Fetches all rated films for the user.</summary>
    internal async Task<List<LetterboxdEntry>> GetRatingsAsync(
        string username, CancellationToken ct)
    {
        return await FetchAllPagesAsync(
            $"{BaseUrl}/{username}/films/ratings/rss/",
            LetterboxdFeedType.Ratings,
            null, ct);
    }

    /// <summary>Validates the username by checking if the diary RSS responds successfully.</summary>
    internal async Task<bool> ValidateUsernameAsync(string username, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync($"{BaseUrl}/{username}/rss/", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ── Pagination loop ───────────────────────────────────────────────────────

    private async Task<List<LetterboxdEntry>> FetchAllPagesAsync(
        string baseUrl,
        LetterboxdFeedType feedType,
        DateTimeOffset? since,
        CancellationToken ct)
    {
        var result = new List<LetterboxdEntry>();
        var page   = 1;

        while (true)
        {
            var url      = page == 1 ? baseUrl : $"{baseUrl}?page={page}";
            var response = await GetWithRateLimitAsync(url, ct);

            if (!response.IsSuccessStatusCode) break;

            var xml     = await response.Content.ReadAsStringAsync(ct);
            var entries = ParseFeed(xml, feedType);

            if (entries.Count == 0) break;

            // Stop early if we've passed the `since` cutoff (entries are newest-first)
            var reachedSince = false;
            foreach (var e in entries)
            {
                if (since.HasValue && e.PublishedAt <= since.Value)
                {
                    reachedSince = true;
                    break;
                }
                result.Add(e);
            }

            if (reachedSince) break;

            page++;
            // Polite delay between pages to avoid hammering the server
            if (!ct.IsCancellationRequested)
                await Task.Delay(PolitenessDelayMs, ct);
        }

        return result;
    }

    // ── RSS parsing ───────────────────────────────────────────────────────────

    private static List<LetterboxdEntry> ParseFeed(string xml, LetterboxdFeedType feedType)
    {
        var result = new List<LetterboxdEntry>();
        var doc    = new XmlDocument();
        doc.LoadXml(xml);

        var nsMgr = new XmlNamespaceManager(doc.NameTable);
        nsMgr.AddNamespace("lbxd", LbxdNs);
        nsMgr.AddNamespace("tmdb", TmdbNs);

        var items = doc.SelectNodes("//item");
        if (items is null) return result;

        foreach (XmlNode item in items)
        {
            var entry = ParseItem(item, nsMgr, feedType);
            if (entry is not null)
                result.Add(entry);
        }

        return result;
    }

    private static LetterboxdEntry? ParseItem(
        XmlNode item, XmlNamespaceManager nsMgr, LetterboxdFeedType feedType)
    {
        // Title format from Letterboxd: "Film Title, 2023" or "Film Title, 2023 - ★★★½"
        var rawTitle   = item.SelectSingleNode("title")?.InnerText ?? string.Empty;
        var link       = item.SelectSingleNode("link")?.InnerText ?? string.Empty;
        var pubDateStr = item.SelectSingleNode("pubDate")?.InnerText;

        // Parse published date
        if (!DateTimeOffset.TryParse(pubDateStr, out var pubDate))
            pubDate = DateTimeOffset.UtcNow;

        // Parse film title and year from the RSS title
        var (filmTitle, filmYear) = ParseTitleAndYear(rawTitle);
        if (string.IsNullOrWhiteSpace(filmTitle)) return null;

        // Extract film slug from the link URL
        // e.g. https://letterboxd.com/user/film/the-matrix/ → the-matrix
        var filmSlug = ExtractFilmSlug(link);

        // Parse rating (letterboxd:memberRating or from title "★★★½")
        double? rating = null;
        var memberRatingNode = item.SelectSingleNode("lbxd:memberRating", nsMgr);
        if (memberRatingNode != null && double.TryParse(
            memberRatingNode.InnerText,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedRating))
            rating = parsedRating;
        else
            rating = ParseStarRatingFromTitle(rawTitle);

        // Rewatch flag
        var rewatchNode = item.SelectSingleNode("lbxd:rewatch", nsMgr);
        var isRewatch   = rewatchNode?.InnerText?.Equals("Yes", StringComparison.OrdinalIgnoreCase) == true;

        return new LetterboxdEntry(
            Title:       filmTitle,
            Year:        filmYear,
            FilmUrl:     link,
            FilmSlug:    filmSlug,
            Rating:      rating,
            PublishedAt: pubDate,
            IsRewatch:   isRewatch,
            FeedType:    feedType);
    }

    // ── Parsing helpers ───────────────────────────────────────────────────────

    // Matches "Film Title, 2023" or "Film Title, 2023 - ★★★"
    private static readonly Regex TitleYearRegex =
        new(@"^(.+),\s*(\d{4})(?:\s*-\s*.*)?$", RegexOptions.Compiled);

    private static (string Title, int? Year) ParseTitleAndYear(string rawTitle)
    {
        // Strip star ratings at the end first
        var withoutStars = Regex.Replace(rawTitle, @"\s*-\s*[★½\s]+$", "").Trim();
        var match = TitleYearRegex.Match(withoutStars);
        if (match.Success)
        {
            var title = match.Groups[1].Value.Trim();
            var year  = int.TryParse(match.Groups[2].Value, out var y) ? y : (int?)null;
            return (title, year);
        }
        return (rawTitle.Trim(), null);
    }

    // Extracts "/film/the-matrix/" slug from diary/watchlist/ratings links
    private static readonly Regex FilmSlugRegex =
        new(@"/film/([^/]+)/", RegexOptions.Compiled);

    private static string? ExtractFilmSlug(string url)
    {
        var match = FilmSlugRegex.Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    // Parses star ratings from title suffix ("★★★½" = 3.5 out of 5)
    private static double? ParseStarRatingFromTitle(string rawTitle)
    {
        var match = Regex.Match(rawTitle, @"-\s*(★+)(½?)$");
        if (!match.Success) return null;
        double stars = match.Groups[1].Value.Length;
        if (match.Groups[2].Value == "½") stars += 0.5;
        return stars;
    }

    private static string? ExtractFilmUrlFromLink(string link) =>
        string.IsNullOrWhiteSpace(link) ? null : link;

    // ── Rate-limit handling ───────────────────────────────────────────────────

    private async Task<HttpResponseMessage> GetWithRateLimitAsync(
        string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfterSec = 30;
            if (response.Headers.RetryAfter?.Delta.HasValue == true)
                retryAfterSec = (int)response.Headers.RetryAfter.Delta!.Value.TotalSeconds + 1;

            await Task.Delay(retryAfterSec * 1000, ct);
            response = await _http.GetAsync(url, ct);
        }

        return response;
    }

    public void Dispose() => _http.Dispose();
}
