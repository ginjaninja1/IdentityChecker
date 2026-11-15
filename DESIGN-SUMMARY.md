# Implementation Handoff: Emby Series Identity Resolver

This document is an implementation specification for a new coding chat. Build the feature in this workspace's Emby plugin template. Do not ask the user additional design questions; decisions and defaults are fixed below.

## Product objective

Create an Emby plugin that can run before standard TV series metadata providers, inspect all candidate matches from configured provider(s), select a better TVDB/TMDB identity than the provider's first result, and pass only the selected provider ID(s) into Emby's normal refresh pipeline. Emby’s stock providers remain responsible for fetching and merging the metadata.

Motivating case: folder `Honey` was assigned TVDB `Money Honey` because TVDB search returned it first, even though a later result had the exact title. The resolver should rank candidates using title similarity and year evidence, and should abstain if evidence is ambiguous.

## Repository implementation context

The repository is a C# `netstandard2.0` plugin project at `src/IdentityChecker/IdentityChecker.csproj`, currently referencing `MediaBrowser.Server.Core` 4.9.1.90. The supplied server/provider decompiles are Emby 4.11.0.12 and are authoritative for runtime behavior in this design. The referenced SDK package may expose a different compile-time surface; use the decompile to define behavior and inspect the package only where needed to resolve compilation or accessibility.

Existing template types to adapt:

- `src/IdentityChecker/Plugin.cs`: plugin entry point already injects `IServerApplicationHost` and `ILogManager`, creates an `ILogger` with `logManager.GetLogger(this.Name)`, and exposes GenericUI pages. Rename its public plugin name/description from “Poster To Folder” to “Series Identity Resolver” (or similarly clear identity-resolver branding) as part of implementing this plugin.
- `src/IdentityChecker/Configuration/PluginConfiguration.cs`: persisted settings via `BasePlugin<PluginConfiguration>` XML configuration. Add resolver settings here, with safe defaults.
- `src/IdentityChecker/UI/Config/ConfigUI.cs`, `ConfigViewBuilder.cs`, `ConfigPageView.cs`, `MainPageController.cs`: existing GenericUI configuration-page pattern. Adapt these rather than invent a separate web UI mechanism.
- The project has unrelated poster-copy task/service code. Do not invoke it from the identity resolver. Remove/replace only if needed for this product; preserve unrelated template work unless a direct conflict exists.

## Relevant Emby types and concrete flow from the decompiles

### Provider registration and the name shown in Emby

The registration path visible in the Emby 4.11.0.12 server decompile and Emby's automatic type discovery documentation is:

1. `ApplicationHost` scans loaded assemblies for concrete types, then `GetExportTypes<T>()` keeps types assignable to `T`; `GetExports<T>()` constructs them.
2. During application startup, `ApplicationHost` passes `GetExports<IMetadataProvider>()` to `ProviderManager.AddParts(...)` (along with exports for image providers, metadata services, metadata savers, and external IDs).
3. `ProviderManager.AddParts(...)` stores the supplied metadata-provider instances in its `_metadataProviders` array. Its search and refresh methods filter this list by implemented interfaces/item type and library options.

Therefore the custom provider class is discovered because it implements an Emby provider interface; it does not need to be manually instantiated in `Plugin` or added to a plugin-local list. Its constructor dependencies are supplied by Emby's dependency injection. The discovery documentation says to implement the appropriate interface and the type becomes part of the system. The manager only receives instances returned by `GetExports<IMetadataProvider>()`.

The provider's displayed/configured name is its own `IMetadataProvider.Name` property, for example:

```csharp
public string Name => "Series Identity Resolver";
```

It is **not** the plugin's `Plugin.Name`. Give the provider a stable, unique name; library metadata-fetcher order and provider search filtering refer to this name. The plugin `Plugin.Name` is the plugin identity shown in plugin management/logging and should separately use the product name.


The supplied Emby 4.11.0.12 decompile confirms the concrete mechanism: `ApplicationHost` builds `AllConcreteTypes` by scanning loaded assemblies for concrete classes, `GetExportTypes<T>()` filters using `typeof(T).IsAssignableFrom(type)`, `GetExports<T>()` constructs those types, and startup passes `GetExports<IMetadataProvider>()` to `ProviderManager.AddParts(...)`. No `[Export]` attribute or plugin-local provider registration is involved. Emby can only construct the discovered type if its constructor dependencies can be resolved; follow the example/current SDK's supported constructor pattern. The authoritative runtime test remains confirming the unique provider `Name` appears in Series Metadata Downloaders and its methods are called.

Useful docs: [Emby's automatic type discovery](https://github.com/MediaBrowser/Emby/wiki/Automatic-Type-Discovery) and [dependency injection](https://github.com/MediaBrowser/Emby/wiki/Dependency-Injection). The community TVMaze provider's `GetSearchResults` example is discussed in [this Emby developer thread](https://emby.media/community/topic/134425-sdk-documentation/?comment=1406215&do=findComment).

### Reference provider pattern: TVMaze

The supplied TVMaze source ZIP implements `IRemoteMetadataProvider<Series, SeriesInfo>` plus `IHasOrder` and uses the older Emby SDK 3.0.684 / .NET Framework 4.5. It is useful for shape and workflow, not current binary signatures. The supplied 4.11.0.12 TVDB/TMDB provider decompiles are authoritative. The current `IRemoteMetadataProviderWithOptions<Series, SeriesInfo>` extends `IRemoteMetadataProvider`, which itself includes the `IRemoteSearchProvider<SeriesInfo>` contract; implement the simple `GetMetadata(SeriesInfo, ...)`, options overload, and `GetSearchResults(SeriesInfo, ...)` as required by those interfaces.

The TVMaze implementation follows the standard two-method provider pattern:

- `GetSearchResults(SeriesInfo, CancellationToken)` is the interactive search path. If an identifying ID is already present, it can return the identified item; otherwise it performs a name/year search and may return several candidates for the user to choose from.
- `GetMetadata(SeriesInfo, CancellationToken)` is the unattended library-refresh path. It must be conservative: use existing provider IDs where possible; if searching by title/year, only accept a match when sufficiently certain. There is no user in this path to resolve ambiguity.
- The provider returns `RemoteSearchResult` values with `Name`, `SearchProviderName`, and provider IDs for search. The selected identity is later passed to metadata fetch.
- The provider class implements Emby interfaces and declares `Name`; its class is auto-discovered. Provider `Name` is what the library downloader order references; plugin `Plugin.Name` is separate.

For this resolver, implement separate behaviors for these methods. `GetSearchResults` should return all ranked candidates, best first, for manual Identify UI; it should not silently mutate the item. `GetMetadata` should run the confidence/abstention policy and return an ID-only `MetadataResult<Series>` only when `ApplySelectedIds` is enabled and the match clears thresholds. In dry-run, it logs the proposed selection but returns no identity. Current source/result limitation matters here too: Emby's manager merges candidate IDs from later providers only when IDs identify an already-returned result. The plugin should call the manager once per source and provide its own ranked list if it wants complete multi-source manual choices.

### Inputs are prepared by Emby

- `Emby.Server.Implementations.Library.Resolvers.TV.SeriesResolver.CreateSeries(FileSystemMetadata folder, string seriesName)` starts with `Name = folder.Name`, then calls `ILibraryManager.ParseName(series.Name)` and stores the parsed `Name` and `Year` as `Series.Name` and `ProductionYear`.
- That resolver's `SetProviderIdsFromPath(Series item)` reads IDs embedded in the folder name/path (`tmdb`, `tvdb`, IMDb).
- `MediaBrowser.Controller.Entities.BaseItem.GetItemLookupInfo<T>` populates lookup properties including `Name`, `Year`, `MetadataLanguage`, `MetadataCountryCode`, `Path`, and `ProviderIds`.
- `Series.GetLookupInfo(LibraryOptions)` returns `SeriesInfo`, which adds series-specific lookup fields.
- Therefore the custom provider consumes `SeriesInfo.Name`, `.Year`, `.ProviderIds`, `.MetadataLanguage`, `.MetadataCountryCode`, and `.Path`. Do not re-parse `Path` for the normal series folder case. Never log full filesystem paths by default; use a safe shortened path or item name.

### Candidate search API

The provider manager's public interface in the decompile is:

```csharp
Task<IEnumerable<RemoteSearchResult>> GetRemoteSearchResults<
    TItemType, TLookupType>(
    RemoteSearchQuery<TLookupType> searchInfo,
    BaseItem referenceItem,
    CancellationToken cancellationToken)
```

There is also an overload without `referenceItem`. It accepts `RemoteSearchQuery<SeriesInfo>`, whose fields include `SearchInfo`, `Providers`, `SearchProviderName`, and `IncludeDisabledProviders`.

Use `IProviderManager.GetRemoteSearchResults<Series, SeriesInfo>` to query standard registered search providers, specifying only `TheTVDB`, only `TheMovieDb`, or both according to configuration. Provider names verified from source: `TheTVDB` and `TheMovieDb`. The manager calls each search provider and tags each result with `RemoteSearchResult.SearchProviderName`.

**Important limitation of the manager search path:** the decompiled `ProviderManager.GetRemoteSearchResults` filters providers by `SearchProviderName` and `Providers`, but `Providers` does not mean “return every provider's full result set.” The manager adds all results from the first provider that returns any, then only merges later-provider results when they share a unique provider ID with an already-added result. Unmatched results from later providers are discarded. This would hide candidates in `Both` mode. Query each source separately by setting `SearchProviderName` to exactly `TheTVDB` in one call and `TheMovieDb` in another, then rank/merge the two complete result sets in the plugin. The manager will also filter out disabled providers unless `IncludeDisabledProviders=true`; leave that false and log when the requested source is disabled or yields no results.

Use the overload with `referenceItem` while refreshing a real library item. The decompiled manager uses that item's library options and preferred language/country; without it, the manager uses default library options and server-wide preferences. It also fills missing language arrays and parses name/year when absent. Pass Emby's prepared `SeriesInfo`, including provider IDs and metadata language/country. Explicitly set the exact `SearchProviderName` filter to prevent the custom provider from querying itself. Note that the shown `GetRemoteSearchResults` signature has no `SeriesInfo` parameter: it accepts `RemoteSearchQuery<SeriesInfo>` and optionally a `BaseItem referenceItem`. Do not describe passing a lookup object as that `referenceItem` argument.

#### Exact stock API query behavior confirmed from provider source

- **TVDB:** `TvdbBaseProvider.FindItem` first tries cross IDs (IMDb, TMDB, Zap2it) when present; otherwise it searches by `SeriesInfo.Name`, passing mapped TVDB languages and optional year. The URL is `https://api4.thetvdb.com/v4/search?type=series&q=<UrlEncode(name)>[&year=<year>]`. A title request without a year is `/search?type=series&q=<encoded-name>`. If there are no results, TVDB normalizes punctuation/parenthetical text and retries. It maps the response `data` array in source order. `TvdbSeriesProvider.GetMetadata` selects `.FirstOrDefault()` when no TVDB ID exists, then fetches using that ID.
- **TMDB:** `MovieDbSeriesProvider.GetSearchResults` first resolves supplied TMDB/IMDb/TVDB IDs by external ID; otherwise it calls `MovieDbSearch` with the series name, year, language list, and adult setting. The series URL is `https://api.themoviedb.org/3/search/tv?api_key=<bundled-key>&query=<UrlEncode(name)>&language=<language>[&first_air_date_year=<year>][&include_adult=true]`. TMDB `results` are mapped in response order. If a year is supplied, it is sent as `first_air_date_year`; when no result is found, the provider can retry with a normalized name. `MovieDbSeriesProvider.GetMetadata` selects the first result if no TMDB ID was found, then fetches details by that ID.
- **Language:** TVDB calls `EntryPoint.ConvertToTvdbLanguages(SeriesInfo)`; TMDB calls `GetMovieDbMetadataLanguages` using lookup language and TMDB-supported languages. The provider manager fills `MetadataLanguage` from the library preferred metadata language when blank. Neither search method uses the server display language as its search-language input.
- The built-in providers own request handling, credentials, language mapping, and response parsing. The custom provider should invoke them through `IProviderManager.GetRemoteSearchResults`, not extract their keys or reproduce their HTTP code. This public manager returns mapped `RemoteSearchResult` objects, not raw HTTP status/body/JSON. The requested diagnostic logging can show provider, query inputs, count, order, mapped titles/dates/IDs, and failures that escape to the manager; it cannot expose the raw API response through this API. Obtaining raw payloads would require duplicating/intercepting HTTP or modifying the built-in provider, which is outside the ID-only resolver design.

Critical safety requirement: because the custom plugin will itself be a series remote search/metadata provider, exclude its own provider name explicitly from every query. Never allow recursive self-search. Set exact `SearchProviderName` per source call (`TheTVDB` or `TheMovieDb`); use `Providers` as an additional allow-list if useful.

In the decompiled 4.11.0.12 implementation, enabled provider selection is checked against library metadata-fetcher settings, and `IncludeDisabledProviders` can bypass that filter. Keep `IncludeDisabledProviders=false` by default. Use the reference-item overload to respect the relevant library's enabled settings; log when an enabled source returns no candidates. Never implicitly enable disabled providers.

### Custom provider contract

Implement a TV series provider recognized by the provider manager, preferably:

```csharp
IRemoteMetadataProviderWithOptions<Series, SeriesInfo>
IHasOrder
```

This interface entails the series metadata provider and remote-search provider contracts. Implement whatever extra interfaces the SDK requires. Key methods to inspect/use:

```csharp
Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
    SeriesInfo searchInfo,
    CancellationToken cancellationToken);

Task<MetadataResult<Series>> GetMetadata(
    RemoteMetadataFetchOptions<SeriesInfo> options,
    CancellationToken cancellationToken);

Task<MetadataResult<Series>> GetMetadata(
    SeriesInfo searchInfo,
    CancellationToken cancellationToken);
```

The stock TVDB/TMDB providers implement `IRemoteMetadataProviderWithOptions<Series, SeriesInfo>` and `IRemoteSearchProvider<SeriesInfo>`. Their `GetMetadata` implementation falls back to their own candidate search and `.FirstOrDefault()` when their own ID is absent. The resolver must seed chosen IDs before those providers execute.

## Candidate selection algorithm (initial fixed policy)

Implement a pure/testable selector, independent of Emby APIs, e.g. `SeriesMatchScorer` and `SeriesCandidateSelector` under `src/IdentityChecker/Matching/`.

### Normalization and title score

- Normalize Unicode to Form KC, lowercase invariantly, replace punctuation/separator runs with spaces, collapse whitespace, trim.
- Preserve letters and digits. Do not discard meaningful words such as “Money”.
- Compute Levenshtein edit distance over normalized titles.
- `titleSimilarity = 1 - distance / max(normalizedInput.Length, normalizedCandidate.Length)`; if both are empty, score 0.
- Exact normalized match is 1.0.

### Year score

- If the input has a year and a candidate has a year: `yearScore = max(0, 1 - abs(inputYear - candidateYear) / 5.0)`. Exact year is 1; a 1-year delta is 0.8; 5 or more is 0.
- If the input year is missing, score candidate years relative to the current UTC year: current/future year = 1; reduce the score by 0.1 per year of age to a floor of 0; missing candidate year = 0.5. Apply this recency score only as a tie-break between exact normalized title matches, after total score and exact-title ordering. This makes current/recent exact-title matches preferable while preserving yearless matches as neutral and keeping fuzzy title similarity in control.
- If input year exists but candidate year is missing, yearScore = 0.

### Initial weights and abstention

- `totalScore = 0.8 * titleSimilarity + 0.2 * yearScore`.
- Select only if the top candidate score is at least `0.72` and exceeds the second candidate by at least `0.08`.
- Otherwise abstain: return no ID, log the ranked choices and reason, and leave Emby's standard behavior untouched. These values are initial, configurable defaults, not claimed to be optimal.
- Deduplicate candidates by matching provider ID. Keep source provenance (`SearchProviderName`) and all known cross-provider IDs.
- With both sources, union candidates and join records only by verified cross-provider IDs. Do not auto-join on fuzzy title/year alone. If source results cannot be joined, score independently by candidate and only return IDs for a selected source result. Do not return mismatched TMDB and TVDB IDs together.
- Tie-break deterministically: higher total score, then exact title match, then smaller year delta (when input year is available), then configured source preference (default TVDB), then original provider result order. Record tie-break reason.

## Source defaults and behavior decisions (resolved; do not ask again)

- Default source mode: **TVDB only**, matching the initial problem and minimizing external queries.
- Supported modes: `Tvdb`, `Tmdb`, `Both`.
- Query only stock sources enabled as metadata downloaders for this library. Do not set `IncludeDisabledProviders` by default.
- `Both` mode merges only candidates joined by exact cross-provider IDs; no fuzzy cross-source join.
- Existing usable provider ID: skip automatic correction and preserve it. Never overwrite an existing/manual ID in the initial implementation.
- Auto mode default: **DryRun**. It computes and logs proposed selections but returns no ID. Provide an explicit `ApplySelectedIds` setting, default false, to turn on ID seeding after diagnostics.
- Low confidence/near tie: abstain and log. No interactive review queue in initial scope.
- No input year: no recency preference; neutral year component.
- If positioned after stock providers: diagnostic only/no correction. Documentation must advise placing ahead of selected stock providers for auto-correction.
- Credentials: never ask for or store user API keys. Use Emby's registered provider search services and standard bundled provider credentials. Do not copy keys from the decompile or access provider-private fields.

## Logging implementation

The existing `Plugin` constructor already injects `ILogManager` and creates a named `ILogger`:

```csharp
this.logger = logManager.GetLogger(this.Name);
```

Preserve this pattern, rename plugin identity from the template's “Poster To Folder”, and pass/use the logger in the custom provider (provider instances are discovered/constructed by Emby; use the SDK's supported injection or the plugin singleton/logger accessor pattern already used in this repository and verify at startup).

Emit these events with one correlation ID per lookup:

- `ResolverStarted`: name, year, existing provider IDs, source mode, dry-run/apply, language/country; no raw full path by default.
- `SearchStarted` / `SearchCompleted`: source provider name, exact lookup fields passed to Emby, duration, status, count, and any exception class/status.
- `Candidate`: response position, provider name, title, candidate year/date, provider IDs. Do not log overview/body by default.
- `CandidateScore`: normalized titles, edit distance, title score, year delta/score, weighted total, and tie-break flags.
- `ResolverDecision`: selected IDs or abstention, confidence, margin, reason, dry-run/apply.
- `IdSeeded`: only when actually returned; state explicitly that stock metadata fetchers will process the item next.

Never log API keys, bearer tokens, authorization headers, or raw response payloads. Candidate scoring and detail records use Debug; resolver selection summaries with provider IDs use Info. Errors should include provider, correlation ID, exception type/message, and elapsed time.

## Configuration model and page

Extend `PluginConfiguration` with serializable settings and defaults:

```csharp
public bool EnablePlugin { get; set; } = true;
public SeriesSearchSourceMode SourceMode { get; set; } = SeriesSearchSourceMode.Tvdb;
public bool ApplySelectedIds { get; set; } = false; // dry-run by default
public double TitleWeight { get; set; } = 0.8;
public double YearWeight { get; set; } = 0.2;
public double MinimumScore { get; set; } = 0.72;
public double MinimumScoreMargin { get; set; } = 0.08;
```

Create `SeriesSearchSourceMode` enum values `Tvdb`, `Tmdb`, `Both`. Validate weights (nonnegative, sum > 0) and thresholds (0..1); normalize weights at use time or reject invalid saved values safely.

Adapt `ConfigUI` and `ConfigViewBuilder` to expose enable, source mode, apply/dry-run, weights, and thresholds. Every displayed setting should be copied to/from `PluginConfiguration` through `MainPageController`/existing config flow and saved with the existing `BasePlugin<PluginConfiguration>` mechanism. The config UI is not allowed to contain secrets or API-key fields.

## Implementation milestones

1. **Map compile-time API:** inspect the referenced Emby NuGet API only where exact signatures/member visibility are needed to compile the implementation. The decompile establishes runtime behavior and the target call flow.
2. **Register a diagnostic provider:** add a public series provider implementing the SDK contract; log invocation/ordering and return no IDs. In the decompiled host, `ApplicationHost` calls `GetExports<IMetadataProvider>()`, then `ProviderManager.AddParts` stores those instances. Confirm Emby discovers the provider and shows its `IMetadataProvider.Name` in series downloader order.
3. **Search and log:** inject `IProviderManager`; call `GetRemoteSearchResults<Series, SeriesInfo>` separately per source with exact `SearchProviderName` and the reference-item overload. Verify no recursion and log empty results/disabled sources. Remain dry-run.
4. **Pure selector:** implement normalization, Levenshtein, year score, weights, thresholds, deduplication, tie-breaks, and abstention in isolated classes. Add tests for exact title, “Honey” vs “Money Honey”, year exact/near/far, missing years, duplicate IDs, ties, and empty results.
5. **ID-only handoff:** behind `ApplySelectedIds=false` by default, return `MetadataResult<Series>` with `HasMetadata=true` and only selected ID(s), using provider ID helpers. In the decompiled flow, `MetadataService.ExecuteRemoteProviders` calls `MergeNewData(temp.Item, id)` before the next provider and after merging each successful result; verify stock TVDB/TMDB receives the selected ID and fetches that identity.
6. **Both-source behavior:** add exact-ID candidate joining and disagreement/abstention logging. Ensure no mismatched ID pair is returned.
7. **Configuration UI:** wire actual configuration model/settings through the existing GenericUI controller/view builder. Test persisted values survive reload.
8. **Controlled end-to-end validation:** use a test library/folder and inspect logs: custom provider before stock provider, IDs seeded, stock provider fetch-by-ID URLs, final stored IDs, and normal metadata fields supplied by stock providers. Also test last position, dry run, pre-existing IDs, provider unavailable/errors, and no-result behavior.

## Acceptance criteria

- No user API key required or persisted.
- New series folder input arrives as Emby-prepared `SeriesInfo`; title/year are not independently reparsed.
- Diagnostics show Emby invocation, exact standard-provider searches, ordered result summaries, every score component, final decision, and downstream ID handoff without secrets.
- Dry-run is default and cannot change IDs.
- Apply mode returns only selected provider IDs; standard providers fetch metadata normally by those IDs.
- A provider in last position cannot silently claim to have prevented first-result selection; logs/docs identify that it is diagnostic-only in that order.
- Existing IDs are preserved; no silent correction of manually identified/previously identified series in initial scope.
- Ambiguous/low-confidence matches abstain.
- Configuration page controls source, apply mode, weights, score/margin thresholds, verbosity, and persists values.
- No unresolved questions remain in this handoff; the defaults above are to be implemented unless concrete SDK constraints make a behavior impossible, in which case report the blocking API limitation with evidence.
