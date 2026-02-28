# Chronicle.Plugin.Letterboxd

Chronicle import plugin for [Letterboxd](https://letterboxd.com). Imports your film diary, ratings, and watchlist into Chronicle via Letterboxd's public RSS feeds.

> **Note:** Letterboxd does not provide a public API. This plugin reads the RSS feeds that Letterboxd exposes for every **public** user account. Your Letterboxd account must be set to **public** for imports to work.

## Setup

1. Make sure your Letterboxd account is set to **Public** (Settings → Privacy → Account Privacy).
2. In Chronicle → Plugins, install this plugin and set:
   - **Letterboxd Username** — your Letterboxd username (visible in your profile URL)
3. Go to Chronicle → Settings → Import → Letterboxd and click **Import**.

No OAuth or API key is required.

## Import

| Feed | What is imported |
|---|---|
| **Diary** (Watch History) | All films you've logged, newest first. Supports incremental `?since=` sync. |
| **Ratings** | All films you've rated. Stars (0.5–5.0) are converted to Chronicle's 1–10 scale (×2). |
| **Watchlist** | All films on your Letterboxd watchlist (added as *Plan to Watch*). |

## Rating conversion

Letterboxd uses a 5-star system with half-star increments:

| Letterboxd | Chronicle |
|---|---|
| ½ ★ | 1 |
| ★ | 2 |
| ★★ | 4 |
| ★★★ | 6 |
| ★★★★ | 8 |
| ★★★★★ | 10 |

## Limitations

- **Public accounts only** — private Letterboxd accounts cannot be accessed via RSS.
- **Movies only** — Letterboxd tracks films; TV shows are not supported by this plugin.
- **No real-time sync** — RSS feeds update when Letterboxd processes them (usually within minutes of logging).
- **Pagination** — Letterboxd RSS pages at 50 items. Large libraries take longer to import.

## Building

```bash
dotnet build
dotnet publish -c Release -o dist/
```

Copy the contents of `dist/` (including `manifest.json`) into your Chronicle `plugins/` directory.
