Shoko Seasons

A separate Shoko server plugin for browsing all seasonal anime in a poster-card catalog, switching seasons and years, and requesting series through Sonarr.

Requirements
- Shoko 6.0.0-dev.550 / Shoko.Abstractions 6.0.0-alpha.112 (.NET 10).
- Shokarr with a configured Sonarr URL, API key, quality profile and root folder.
- Sonarr's destination must be a folder Shoko watches. Sonarr handles downloads and imports; Shoko must identify the files before they are called ready.
- Internet access from Shoko to graphql.anilist.co and raw.githubusercontent.com. Posters load from AniList's image CDN in your browser.

Installation
Add the following manifest URL to Shoko's plugin manager, install Shoko Seasons and restart Shoko:
https://raw.githubusercontent.com/alekso56/Shoko-MPC-HC/feature/seasonal-anime/manifest.seasons.json

Open Seasonal anime from the Web UI shortcut or the plugin page. The plugin adds its own navigation shortcut without patching Shoko files. Removing the plugin removes the shortcut. It is independent of the MPC-HC plugin.

The dashboard also shows a Seasonal anime card above the built-in cards, with the full season picker, catalog, requests and notifications. Hide card collapses it; Add seasonal anime card restores it. This preference is saved in this browser. Open full catalog opens the standalone page. Shoko has no plugin widget registry, so this card stays above its draggable layout and is controlled separately from Display Settings. Built-in card positions are preserved.

Use
Choose a season and year, or use the previous/next/This season controls. Search and format filters apply to the complete selected season. Cached seasons reopen immediately. The catalog omits adult-tagged titles. Ratings are AniList scores, displayed out of ten; MAL links open MyAnimeList details.

Add to list is disabled for anime already in the Shoko library, including series entries without playable files. The server also rejects these requests before changing Sonarr. Existing titles keep their Open episodes/series link. Buttons stay disabled until the library check finishes.

For anime outside Shoko, Add to list opens a Sonarr match and season selector. Confirming Add & search monitors and searches the selected TVDB season. An anime cour can share a TVDB season with another cour. Existing monitoring and quality settings on other seasons are preserved. Movies and music entries remain browsable but cannot be added to Sonarr. Download actions require a Shoko administrator.

My list is saved per Shoko user. Progress is Sonarr's byte progress for active downloads of the selected season, not a prediction of whole-season completion. Pack rows are deduplicated. Queued, paused, failed/import-warning, waiting-for-release and waiting-for-Shoko states are distinguished. Sonarr errors preserve the last known state with a stale-status warning. Add requests are idempotent after success; an interrupted or failed search exposes Retry request.

Ready means at least one episode or special for the exact mapped AniDB anime exists in Shoko and is visible to the user. It does not mean the whole season is complete. Missing or ambiguous mappings never claim a title is ready based only on its name. A missing mapping may delay readiness until the mapping provider updates it. Shoko's unrecognized files must be identified in Shoko before they count.

The server checks activity every 30 seconds. The page refreshes status every 10 seconds while visible. New available episodes create saved notifications (latest 100 per user), an unread badge and a toast on the seasonal page. Notifications remain in the inbox after the browser is closed; this is not an operating-system push service. Open episodes links back to Shoko, including the existing MPC-HC buttons if installed.

Privacy and storage
Shoko session credentials are sent in same-origin API headers. The Sonarr API key is read through Shoko's native configuration service and stays server-side. No separate credential entry is needed. Public catalog queries and anime-ID mappings go to AniList and Fribb respectively. Requests/notifications are stored under the plugin UUID in Shoko's DatabasePath; rebuildable catalog and mapping data use CachePath. Catalog entries are cached six hours and mappings one day.

Build and validation
Run npm ci in Tests, then ./build-seasons.ps1 with the .NET 10 SDK and Node installed. The build runs backend and DOM tests and creates the plugin ZIP plus manifest.seasons.json. Tests use fake provider responses and do not start real downloads.

Data sources
AniList API: https://docs.anilist.co/
Anime ID mappings: https://github.com/Fribb/anime-lists
Sonarr API: https://sonarr.tv/docs/api/
