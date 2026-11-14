# Series Identity Resolver

An Emby series metadata provider that searches the configured TVDB and/or TMDB stock providers, ranks their results by title and year evidence, and optionally seeds a confident provider ID before Emby's normal metadata providers run. Stock providers remain responsible for fetching series metadata.

## Setup

Install the plugin and configure it from the plugin settings page. The default is TVDB-only dry-run mode: it logs candidate ordering and scoring but returns no IDs. Set **Apply selected IDs** to enable ID seeding. Keep this provider ahead of the selected stock provider in each series library's metadata downloader order; a provider placed later cannot prevent an earlier provider from selecting its first search result.

Existing TVDB or TMDB IDs are preserved. Low-confidence and near-tie matches abstain. In Both mode, candidate records are never joined by fuzzy title similarity; only IDs reported together by a stock result are carried together.

## Search behavior

The provider calls Emby's registered `TheTVDB` and `TheMovieDb` search providers. It never uses private credentials or makes direct API calls. The SDK available to this build exposes the manager search overload without a reference item, so library-specific enabled-provider filtering may follow server defaults until the project is built against a newer SDK exposing the reference-item overload.

## Build

Build `src/IdentityChecker/IdentityChecker.csproj` with the .NET SDK. No post-build deployment occurs; install the generated plugin files using your normal Emby deployment process.
