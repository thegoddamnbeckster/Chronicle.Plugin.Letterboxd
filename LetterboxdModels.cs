namespace Chronicle.Plugin.Letterboxd;

/// <summary>
/// A single entry parsed from a Letterboxd RSS feed item.
/// One model covers diary entries, ratings and watchlist entries —
/// the FeedType determines which fields are populated.
/// </summary>
internal record LetterboxdEntry(
    /// <summary>The film title.</summary>
    string  Title,

    /// <summary>Release year extracted from the title string ("Film Title, 2023").</summary>
    int?    Year,

    /// <summary>Letterboxd film page URL — used as the primary external identifier.</summary>
    string? FilmUrl,

    /// <summary>TMDB movie ID extracted from the film page link (e.g. /film/the-matrix/ → slug).</summary>
    string? FilmSlug,

    /// <summary>User rating on a 0.5–5.0 scale (★½ increments). Null if not rated.</summary>
    double? Rating,

    /// <summary>Date the entry was published (watched / rated / listed).</summary>
    DateTimeOffset PublishedAt,

    /// <summary>Whether this entry is a rewatch.</summary>
    bool    IsRewatch,

    /// <summary>Feed type this came from.</summary>
    LetterboxdFeedType FeedType
);

internal enum LetterboxdFeedType
{
    Diary,      // https://letterboxd.com/{username}/rss/ — watched films
    Watchlist,  // https://letterboxd.com/{username}/watchlist/rss/
    Ratings,    // https://letterboxd.com/{username}/films/ratings/rss/
}
