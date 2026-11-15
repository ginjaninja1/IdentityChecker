# Identity Helper - Series

An Emby series metadata provider that searches the configured TVDB and/or TMDB stock providers, ranks their results by title and year evidence, and optionally seeds the highest-ranked available provider ID before Emby's normal metadata providers run. Stock providers remain responsible for fetching series metadata.

## Setup

Install the plugin and configure it from the plugin settings page. The default search source is **Inherit**: the resolver reads the library's enabled series metadata fetchers and their order, searches those sources, and uses their order to break equal-score ties. Set **TVDB**, **TMDB**, or **Both** to override the library selection for development or diagnostics. These explicit modes include disabled providers. The default is dry-run mode: it logs candidate ordering and scoring but returns no IDs. Set **Apply selected IDs** to enable ID seeding. Keep this provider ahead of the selected stock provider in each series library's metadata downloader order; a provider placed later cannot prevent an earlier provider from selecting its first search result.

Existing TVDB or TMDB IDs are preserved. In Both mode, candidate records are never joined by fuzzy title similarity; only IDs reported together by a stock result are carried together.

## Search behavior

The provider calls Emby's registered `TheTVDB` and `TheMovieDb` search providers separately, preserving each complete candidate list. It never uses private credentials or makes direct API calls. When library context is available, it is passed through the search query's `ItemId`, allowing Emby to apply the relevant library options. Automatic refresh resolves the series item from the lookup path. The resolver logs detected enabled fetchers, configured order, sources queried, and tie-break order; if library context cannot be resolved, Inherit falls back to searching both stock providers with TVDB-first tie-breaking.

## Build

Build `src/IdentityChecker/IdentityChecker.csproj` with the .NET SDK. No post-build deployment occurs; install the generated plugin files using your normal Emby deployment process.

