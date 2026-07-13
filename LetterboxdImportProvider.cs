using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Letterboxd;

/// <summary>
/// Chronicle import plugin for Letterboxd.
///
/// Letterboxd does not provide a public API. This plugin reads data from the
/// RSS feeds that Letterboxd exposes for every public user account:
///
///   Diary    https://letterboxd.com/{username}/rss/
///   Watchlist https://letterboxd.com/{username}/watchlist/rss/
///   Ratings  https://letterboxd.com/{username}/films/ratings/rss/
///
/// No OAuth is needed — the user simply provides their Letterboxd username.
/// If their account is private, feeds will return 404 and the import will fail
/// with a clear error message directing them to make their account public.
///
/// Rating conversion:
///   Letterboxd uses ★ (1–5 in 0.5 increments) → Chronicle uses 1–10.
///   Conversion: rating × 2, rounded to nearest integer.
///
/// Important: This plugin requires the user's Letterboxd profile to be PUBLIC.
/// Letterboxd does not expose RSS for private accounts.
/// </summary>
public sealed class LetterboxdImportProvider : IImportProvider
{
    // ── IImportProvider identity ──────────────────────────────────────────────

    public string PluginId    => "letterboxd";
    public string Name        => "Letterboxd";
    public string Version     => "1.0.0";
    public string Author      => "Michael Beck";
    public string Description =>
        "Import film diary, ratings and watchlist from Letterboxd (public accounts via RSS)";

    // ── Settings keys ─────────────────────────────────────────────────────────

    private const string KeyUsername = "username";

    // ── Runtime state ─────────────────────────────────────────────────────────

    private string? _username;
    private LetterboxdRssClient? _client;

    // ── Settings schema ───────────────────────────────────────────────────────

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key         = KeyUsername,
                Label       = "Letterboxd Username",
                Description = "Your Letterboxd username (from your profile URL: letterboxd.com/{username}). " +
                              "Your account must be public for imports to work.",
                Type        = SettingType.Text,
                Required    = true,
            },
        ]
    };

    // ── Configure ─────────────────────────────────────────────────────────────

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        settings.TryGetValue(KeyUsername, out _username);
        _username = _username?.Trim().ToLowerInvariant();

        _client?.Dispose();
        _client = new LetterboxdRssClient();
    }

    // ── Auth — not applicable ─────────────────────────────────────────────────
    //
    // Letterboxd RSS feeds are publicly accessible for public accounts.
    // There is no OAuth or API key — the username IS the credential.
    // StartAuthAsync/PollAuthAsync are not used; IsAuthenticatedAsync checks
    // that a username has been configured and the feed responds.

    public Task<DeviceAuthStart> StartAuthAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Letterboxd does not use an OAuth flow. " +
            "Set your Letterboxd username in the plugin settings.");

    public Task<DeviceAuthPollResult> PollAuthAsync(
        string pollCode, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Letterboxd does not use an OAuth poll flow.");

    public async Task<bool> IsAuthenticatedAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_username)) return false;
        var client = GetOrCreateClient();
        return await client.ValidateUsernameAsync(_username, ct);
    }

    // ── Capabilities ──────────────────────────────────────────────────────────

    public ImportCapabilities GetCapabilities() =>
        new(SupportsHistory: true, SupportsRatings: true, SupportsWatchlist: true,
            RequiresDeviceAuth: false);

    // ── Import — history (diary) ──────────────────────────────────────────────

    public async Task<List<ImportedWatchEvent>> GetWatchHistoryAsync(
        DateTimeOffset? since = null, CancellationToken ct = default)
    {
        EnsureUsername();
        var client  = GetOrCreateClient();
        var entries = await client.GetDiaryAsync(_username!, since, ct);

        return entries.Select(e => new ImportedWatchEvent(
            ExternalId:      $"letterboxd-slug:{e.FilmSlug ?? e.Title}",
            AdditionalIds:   BuildIds(e),
            MediaType:       "movie",
            Title:           e.Title,
            Year:            e.Year,
            WatchedAt:       e.PublishedAt,
            ProgressPercent: 100.0
        )).ToList();
    }

    // ── Import — ratings ──────────────────────────────────────────────────────

    public async Task<List<ImportedRating>> GetRatingsAsync(CancellationToken ct = default)
    {
        EnsureUsername();
        var client  = GetOrCreateClient();
        var entries = await client.GetRatingsAsync(_username!, ct);

        var result = new List<ImportedRating>();
        foreach (var e in entries)
        {
            if (!e.Rating.HasValue) continue;

            // Convert Letterboxd 0.5–5 scale to Chronicle 1–10 scale.
            // 0.5★ → 1,  1★ → 2,  2.5★ → 5,  5★ → 10
            var chronicleRating = (int)Math.Round(e.Rating.Value * 2, MidpointRounding.AwayFromZero);
            chronicleRating = Math.Clamp(chronicleRating, 1, 10);

            result.Add(new ImportedRating(
                ExternalId:    $"letterboxd-slug:{e.FilmSlug ?? e.Title}",
                AdditionalIds: BuildIds(e),
                MediaType:     "movie",
                Title:         e.Title,
                Year:          e.Year,
                Rating:        chronicleRating,
                RatedAt:       e.PublishedAt));
        }

        return result;
    }

    // ── Import — watchlist ────────────────────────────────────────────────────

    public async Task<List<ImportedWatchlistEntry>> GetWatchlistAsync(CancellationToken ct = default)
    {
        EnsureUsername();
        var client  = GetOrCreateClient();
        var entries = await client.GetWatchlistAsync(_username!, ct);

        return entries.Select(e => new ImportedWatchlistEntry(
            ExternalId:    $"letterboxd-slug:{e.FilmSlug ?? e.Title}",
            AdditionalIds: BuildIds(e),
            MediaType:     "movie",
            Title:         e.Title,
            Year:          e.Year,
            AddedAt:       e.PublishedAt
        )).ToList();
    }

    // ── Health check ──────────────────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default) =>
        await IsAuthenticatedAsync(ct);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, string> BuildIds(LetterboxdEntry e)
    {
        var d = new Dictionary<string, string>();
        if (e.FilmSlug is not null) d["letterboxd-slug"] = e.FilmSlug;
        if (e.FilmUrl  is not null) d["letterboxd-url"]  = e.FilmUrl;
        return d;
    }

    private void EnsureUsername()
    {
        if (string.IsNullOrWhiteSpace(_username))
            throw new InvalidOperationException(
                "Letterboxd username is not configured. " +
                "Set it via Plugins → Letterboxd → Settings.");
    }

    private LetterboxdRssClient GetOrCreateClient() =>
        _client ??= new LetterboxdRssClient();
}
