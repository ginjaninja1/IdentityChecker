using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IdentityHelper.Configuration;
using IdentityHelper.Matching;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;

namespace IdentityHelper.Providers
{
    /// <summary>
    /// Emby auto-discovers this class from its series remote metadata provider interface.
    /// Manual Identify search ranks TVDB/TMDB results using the plugin's manual source setting. During an
    /// automatic refresh (library scan or ingestion) it resolves the series' library from the item path, reads
    /// that library's Series fetcher configuration, and returns any TVDB/TMDB IDs it seeds. User-initiated
    /// refreshes, including the apply phase of a manual Identify, are left untouched. Emby merges returned IDs
    /// into the lookup info handed to the providers that run after this one, so this provider must be ordered
    /// ahead of the stock fetchers in the library's fetcher order.
    /// </summary>
    public sealed class SeriesIdentityHelperProvider : IRemoteMetadataProviderWithOptions<Series, SeriesInfo>, IHasOrder
    {
        private const string ProviderName = "Identity Helper - Series";
        private const string Tvdb = "TheTVDB";
        private const string Tmdb = "TheMovieDb";
        private const string TvdbId = "Tvdb";
        private const string TmdbId = "Tmdb";
        private readonly IProviderManager providerManager;
        private readonly ILibraryManager libraryManager;
        private readonly ILogger logger;

        public SeriesIdentityHelperProvider(IProviderManager providerManager, ILibraryManager libraryManager, ILogManager logManager)
        {
            this.providerManager = providerManager;
            this.libraryManager = libraryManager;
            this.logger = logManager.GetLogger(ProviderName);
        }

        public string Name => ProviderName;
        public int Order => 0;

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            // ProviderManager stamps SearchProviderName with this provider's name after
            // GetSearchResults returns. Forward preview requests to the stock provider
            // that owns the URL so Emby can display the selected result's image.
            var source = url != null && url.IndexOf("themoviedb.org/", StringComparison.OrdinalIgnoreCase) >= 0
                ? Tmdb
                : Tvdb;
            return this.providerManager.GetSearchImage(source, url, cancellationToken);
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            var config = GetConfiguration();
            var correlation = Guid.NewGuid().ToString("N").Substring(0, 8);
            LogSearchInput(correlation, searchInfo, config);
            if (!config.EnablePlugin)
            {
                this.logger.Info("ResolverStarted {0}: pathway=manual-search, disabled=true; returning=0", correlation);
                return Enumerable.Empty<RemoteSearchResult>();
            }
            // Emby's manual search callback receives only SeriesInfo; its API ItemId is not forwarded,
            // so library options are unavailable here and the plugin's own manual source setting applies.
            var sourcePlan = GetManualSourcePlan(config, correlation);
            this.logger.Info("ResolverStarted {0}: pathway=manual-search, name={1}, year={2}, source={3}, language={4}, country={5}",
                correlation, searchInfo?.Name, searchInfo?.Year, config.ManualSource, searchInfo?.MetadataLanguage, searchInfo?.MetadataCountryCode);
            var found = await Search(searchInfo, 0, sourcePlan.Sources,
                sourcePlan.IncludeDisabledProviders, cancellationToken).ConfigureAwait(false);
            var selection = SeriesCandidateSelector.Select(searchInfo.Name, searchInfo.Year, found.Select(x => x.Candidate), config, sourcePlan.TieBreakOrder);
            LogSelection(correlation, selection);
            var selected = selection.Selected?.Candidate;
            this.logger.Info("ResolverDecision {0}: manual-search returned={1}, selected={2}, source={3}, tvdb={4}, tmdb={5}, reason={6}",
                correlation, selection.Ranked.Count, selected?.Name ?? "none", selected?.Source ?? "none",
                selected?.TvdbId ?? "none", selected?.TmdbId ?? "none", selection.Reason);
            return selection.Ranked
                .Select(x => found.First(y => ReferenceEquals(y.Candidate, x.Candidate)).SearchResult)
                .ToList();
        }

        public Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            return ResolveMetadata(info, cancellationToken);
        }

        public Task<MetadataResult<Series>> GetMetadata(RemoteMetadataFetchOptions<SeriesInfo> options, CancellationToken cancellationToken)
        {
            return ResolveMetadata(options.SearchInfo, cancellationToken);
        }

        /// <summary>
        /// Seeds TVDB/TMDB IDs for a series that has neither. The result carries only those IDs
        /// and reports metadata only when IDs were seeded.
        /// </summary>
        private async Task<MetadataResult<Series>> ResolveMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Series>();
            var config = GetConfiguration();
            var correlation = Guid.NewGuid().ToString("N").Substring(0, 8);
            if (!config.EnablePlugin) { this.logger.Info("ResolverStarted {0}: disabled", correlation); return result; }
            if (!info.IsAutomated)
            {
                // Manual Identify apply, Refresh metadata and metadata-editor saves are user-driven: the user's
                // choice stands, and identity resolution only runs for automatic scans and ingestion.
                this.logger.Info("ResolverDecision {0}: skipped; reason=user-initiated-refresh", correlation);
                return result;
            }
            var tvdbExisting = GetProviderId(info, TvdbId);
            var tmdbExisting = GetProviderId(info, TmdbId);
            this.logger.Info("ResolverStarted {0}: name={1}, year={2}, tvdb={3}, tmdb={4}, source={5}, language={6}, country={7}",
                correlation, info.Name, info.Year, tvdbExisting, tmdbExisting, config.AutomaticSource,
                info.MetadataLanguage, info.MetadataCountryCode);
            if (!string.IsNullOrWhiteSpace(tvdbExisting) || !string.IsNullOrWhiteSpace(tmdbExisting))
            {
                this.logger.Info("ResolverDecision {0}: preserve existing provider ID", correlation);
                return result;
            }

            var series = FindSeries(info, correlation);
            var libraryOptions = series == null ? null : GetLibraryOptions(series, correlation);
            var sourcePlan = GetLibrarySourcePlan(config, libraryOptions, correlation);
            var found = await Search(info, series?.InternalId ?? 0, sourcePlan.Sources,
                sourcePlan.IncludeDisabledProviders, cancellationToken).ConfigureAwait(false);
            var selection = SeriesCandidateSelector.Select(info.Name, info.Year, found.Select(x => x.Candidate), config, sourcePlan.TieBreakOrder);
            LogSelection(correlation, selection);
            var chosen = selection.Selected;
            if (chosen == null)
            {
                this.logger.Info("ResolverDecision {0}: no candidate available ({1})", correlation, selection.Reason);
                return result;
            }
            this.logger.Info("ResolverDecision {0}: candidate={1}, source={2}, tvdb={3}, tmdb={4}, confidence={5:F3}, margin={6:F3}, reason={7}",
                correlation, chosen.Candidate.Name, chosen.Candidate.Source, chosen.Candidate.TvdbId ?? "none", chosen.Candidate.TmdbId ?? "none",
                chosen.TotalScore, selection.Margin, selection.Reason);

            var seeded = new Series();
            if (!string.IsNullOrWhiteSpace(chosen.Candidate.TvdbId)) seeded.SetProviderId(TvdbId, chosen.Candidate.TvdbId);
            if (!string.IsNullOrWhiteSpace(chosen.Candidate.TmdbId)) seeded.SetProviderId(TmdbId, chosen.Candidate.TmdbId);
            if (seeded.ProviderIds.Count == 0) return result;

            result.Item = seeded;
            result.HasMetadata = true;
            this.logger.Info("IdSeeded {0}: tvdb={1}, tmdb={2}; later providers in the library's fetcher order will receive these IDs", correlation,
                chosen.Candidate.TvdbId, chosen.Candidate.TmdbId);
            return result;
        }

        private void LogSelection(string correlation, SeriesSelection selection)
        {
            foreach (var s in selection.Ranked)
            {
                this.logger.Debug("CandidateScore {0}: provider={1}, title={2}, normalized={3}/{4}, distance={5}, titleScore={6:F3}, yearDelta={7}, yearScore={8:F3}, total={9:F3}",
                    correlation, s.Candidate.Source, s.Candidate.Name, s.NormalizedInput, s.NormalizedCandidate, s.EditDistance,
                    s.TitleSimilarity, s.YearDelta, s.YearScore, s.TotalScore);
                this.logger.Debug("Candidate {0}: source={1}, position={2}, title={3}, year={4}, tvdb={5}, tmdb={6}",
                    correlation, s.Candidate.Source, s.Candidate.Position, s.Candidate.Name, s.Candidate.Year, s.Candidate.TvdbId, s.Candidate.TmdbId);
            }
            if (selection.Selected == null)
            {
                this.logger.Info("ResolverSelection {0}: no candidate available ({1})", correlation, selection.Reason);
                return;
            }
            this.logger.Info("ResolverSelection {0}: candidate={1}, source={2}, tvdb={3}, tmdb={4}, score={5:F3}, margin={6:F3}, reason={7}",
                correlation, selection.Selected.Candidate.Name, selection.Selected.Candidate.Source,
                selection.Selected.Candidate.TvdbId ?? "none", selection.Selected.Candidate.TmdbId ?? "none",
                selection.Selected.TotalScore, selection.Margin, selection.Reason);
        }

        /// <param name="itemId">Library item whose options the provider manager should apply; 0 when none is known.</param>
        private async Task<List<ScoredCandidate>> Search(SeriesInfo info, long itemId, IReadOnlyList<string> sources,
            bool includeConfiguredSourcesDespiteManagerDefaults, CancellationToken cancellationToken)
        {
            var output = new List<ScoredCandidate>();
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var query = new RemoteSearchQuery<SeriesInfo>
                {
                    SearchInfo = info,
                    SearchProviderName = source,
                    ItemId = itemId,
                    IncludeDisabledProviders = includeConfiguredSourcesDespiteManagerDefaults
                };
                var watch = Stopwatch.StartNew();
                this.logger.Info("SearchStarted: provider={0}, name={1}, year={2}, language={3}, country={4}", source,
                    info.Name, info.Year, info.MetadataLanguage, info.MetadataCountryCode);
                try
                {
                    // Emby resolves ItemId to a reference item in ProviderManager, then applies that item's library options.
                    var results = (await this.providerManager.GetRemoteSearchResults<Series, SeriesInfo>(query, cancellationToken).ConfigureAwait(false)
                        ?? Enumerable.Empty<RemoteSearchResult>()).ToList();
                    this.logger.Info("SearchCompleted: provider={0}, status=ok, count={1}, durationMs={2}", source, results.Count, watch.ElapsedMilliseconds);
                    var position = 0;
                    var sourceCandidates = new List<SeriesCandidate>();
                    foreach (var item in results)
                    {
                        position++;
                        var ids = item.ProviderIds ?? new Dictionary<string, string>();
                        var candidate = new SeriesCandidate
                        {
                            Name = item.Name,
                            Year = item.ProductionYear,
                            Source = source,
                            Position = position,
                            TvdbId = ids.ContainsKey(TvdbId) ? ids[TvdbId] : null,
                            TmdbId = ids.ContainsKey(TmdbId) ? ids[TmdbId] : null
                        };
                        // Provider manager may return other IDs; candidate ownership is scoped to the source call.
                        if (source == Tvdb && string.IsNullOrWhiteSpace(candidate.TvdbId)) continue;
                        if (source == Tmdb && string.IsNullOrWhiteSpace(candidate.TmdbId)) continue;
                        sourceCandidates.Add(candidate);
                    }
                    if (sourceCandidates.Count > 0)
                    {
                        var providerSpecificIds = sourceCandidates.Select(c => source == Tvdb ? c.TvdbId : c.TmdbId).ToList();
                        var idsAreSameEntity = sourceCandidates.Count == 1 || providerSpecificIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
                        if (idsAreSameEntity)
                        {
                            var combined = sourceCandidates[0];
                            foreach (var same in sourceCandidates.Skip(1))
                            {
                                if (!string.IsNullOrWhiteSpace(same.TvdbId)) combined.TvdbId = same.TvdbId;
                                if (!string.IsNullOrWhiteSpace(same.TmdbId)) combined.TmdbId = same.TmdbId;
                            }
                            output.Add(new ScoredCandidate
                            {
                                Candidate = combined,
                                SearchResult = FindSearchResult(results, source, combined)
                            });
                        }
                        else output.AddRange(sourceCandidates.Select(c => new ScoredCandidate
                        {
                            Candidate = c,
                            SearchResult = FindSearchResult(results, source, c)
                        }));
                    }
                    if (results.Count == 0) this.logger.Info("SearchCompleted: provider={0}, no candidates (provider may be disabled for this library)", source);
                }
                catch (Exception ex)
                {
                    this.logger.ErrorException("Search failed: provider=" + source + ", elapsedMs=" + watch.ElapsedMilliseconds, ex);
                }
            }
            // Collapse duplicate source IDs and retain both IDs only when returned together by a stock result.
            return output.GroupBy(x => !string.IsNullOrWhiteSpace(x.Candidate.TvdbId) ? "tvdb:" + x.Candidate.TvdbId : "tmdb:" + x.Candidate.TmdbId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToList();
        }

        private void LogSearchInput(string correlation, SeriesInfo info, PluginConfiguration config)
        {
            if (info == null)
            {
                this.logger.Info("ManualSearchInput {0}: info=null, pluginEnabled={1}, sourceMode={2}",
                    correlation, config.EnablePlugin, config.ManualSource);
                return;
            }

            var providerIds = info.ProviderIds == null
                ? "null"
                : string.Join(",", info.ProviderIds.Select(x => x.Key + "=" + x.Value));
            this.logger.Info("ManualSearchInput {0}: type={1}, name={2}, year={3}, path={4}, pathIsNull={5}, metadataLanguage={6}, country={7}, metadataLanguages={8}, providerIds={9}, sourceMode={10}, pluginEnabled={11}",
                correlation, info.GetType().FullName, info.Name ?? "<null>", info.Year,
                info.Path ?? "<null>", info.Path == null, info.MetadataLanguage ?? "<null>",
                info.MetadataCountryCode ?? "<null>", info.MetadataLanguages == null ? "null" : string.Join(",", info.MetadataLanguages.Select(x => x?.ThreeLetterISOLanguageName ?? x?.Name ?? "<null>")),
                providerIds, config.ManualSource, config.EnablePlugin);
        }

        /// <summary>Resolves the library series for a refresh from the lookup info's path; null when it cannot be found.</summary>
        private Series FindSeries(SeriesInfo info, string correlation)
        {
            if (string.IsNullOrWhiteSpace(info?.Path))
            {
                this.logger.Info("LibraryContextLookup {0}: skipped; reason=path-empty", correlation);
                return null;
            }
            try
            {
                var found = this.libraryManager.FindByPath(info.Path, true);
                var series = found as Series;
                if (series != null)
                {
                    this.logger.Info("LibraryContextLookup {0}: FindByPath matched series; itemId={1}, path={2}", correlation, series.InternalId, series.Path);
                    return series;
                }
                this.logger.Info("LibraryContextLookup {0}: FindByPath result={1}; expected=Series, path={2}",
                    correlation, found == null ? "no-item" : found.GetType().FullName, info.Path);
                return null;
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("LibraryContextLookup " + correlation + ": FindByPath threw for path " + info.Path, ex);
                return null;
            }
        }

        private LibraryOptions GetLibraryOptions(Series series, string correlation)
        {
            try
            {
                return this.libraryManager.GetLibraryOptions(series);
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("LibraryProviderConfiguration " + correlation + ": GetLibraryOptions(item) threw", ex);
                return null;
            }
        }

        /// <summary>Manual search has no library context, so sources come solely from the plugin's manual setting.</summary>
        private SourcePlan GetManualSourcePlan(PluginConfiguration config, string correlation)
        {
            var sources = GetSources(config.ManualSource).ToList();
            var tieBreakOrder = config.ManualSource == ManualSeriesSearchSource.Both
                ? GetManualTieBreakOrder(config.ManualPreferOnTie).ToList()
                : sources.ToList();
            if (tieBreakOrder.Count == 0) tieBreakOrder.AddRange(new[] { Tvdb, Tmdb });

            this.logger.Info("LibraryProviderConfiguration {0}: pathway=manual-search, context=not-forwarded-by-manual-provider-contract, sourceMode={1}, selectedSources={2}, tieBreakOrder={3}",
                correlation, config.ManualSource, FormatList(sources), FormatList(tieBreakOrder));
            return new SourcePlan
            {
                Sources = sources,
                TieBreakOrder = tieBreakOrder,
                IncludeDisabledProviders = true
            };
        }

        /// <summary>Refresh pathway (scan and Identify apply): sources and tie-break order follow the library's configured Series fetchers.</summary>
        private SourcePlan GetLibrarySourcePlan(PluginConfiguration config, LibraryOptions options, string correlation)
        {
            var seriesOptions = options?.GetTypeOptions("Series");

            // Library configuration can expose the enabled fetchers in their configured
            // sequence while MetadataFetcherOrder is empty. Use that sequence as the
            // order fallback; otherwise the alphabetical tie-break below would reverse
            // the library's visible provider priority.
            var rawFetcherNames = seriesOptions?.MetadataFetchers ?? Array.Empty<string>();
            var rawFetcherOrder = seriesOptions?.MetadataFetcherOrder ?? Array.Empty<string>();
            var configuredEnabled = rawFetcherNames.Where(IsResolverSource).ToList();
            var effectiveOrder = rawFetcherOrder.Length > 0 ? rawFetcherOrder : rawFetcherNames;
            var orderSource = rawFetcherOrder.Length > 0 ? "MetadataFetcherOrder" :
                rawFetcherNames.Length > 0 ? "MetadataFetchers-sequence-fallback" : "none";
            var configuredOrder = effectiveOrder
                .Where(IsResolverSource).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var inheritedSources = configuredEnabled
                .OrderBy(x => ProviderOrder(configuredOrder, x))
                .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(NormalizeSource)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var mode = config.AutomaticSource;
            List<string> sources;
            if (mode == AutomaticSeriesSearchSource.Inherit)
            {
                // Without library options, fall back to the stock sources.
                sources = options != null ? inheritedSources : new List<string> { Tvdb, Tmdb };
            }
            else
            {
                sources = GetSources(mode).ToList();
            }

            var tieBreakOrder = configuredOrder.Select(NormalizeSource).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (tieBreakOrder.Count == 0) tieBreakOrder.AddRange(inheritedSources);
            if (tieBreakOrder.Count == 0) tieBreakOrder.AddRange(new[] { Tvdb, Tmdb });

            this.logger.Info("LibraryProviderConfiguration {0}: pathway=refresh, optionsResolved={1}, seriesTypeOptions={2}, sourceMode={3}, detectedEnabled={4}, detectedOrder={5}, detectedOrderSource={6}, metadataFetcherOrderRaw={7}, metadataFetcherNamesRaw={8}, inheritedSources={9}, selectedSources={10}, tieBreakOrder={11}, fallbackReason={12}",
                correlation, options != null, seriesOptions != null, mode,
                options == null ? "<unavailable>" : FormatList(configuredEnabled),
                options == null ? "<unavailable>" : FormatList(configuredOrder),
                options == null ? "<unavailable>" : orderSource,
                options == null ? "<unavailable>" : FormatList(rawFetcherOrder),
                options == null ? "<unavailable>" : FormatList(rawFetcherNames),
                FormatList(inheritedSources), FormatList(sources), FormatList(tieBreakOrder),
                options != null ? "none" : mode == AutomaticSeriesSearchSource.Inherit ? "no-library-options" : "explicit-source-mode");
            return new SourcePlan
            {
                Sources = sources,
                TieBreakOrder = tieBreakOrder,
                IncludeDisabledProviders = mode != AutomaticSeriesSearchSource.Inherit
            };
        }

        private static string FormatList(IEnumerable<string> values)
        {
            var list = values?.ToList() ?? new List<string>();
            return list.Count == 0 ? "<empty>" : string.Join(",", list);
        }

        private static IReadOnlyList<string> GetManualTieBreakOrder(SeriesSearchTieBreakPreference preference)
        {
            return preference == SeriesSearchTieBreakPreference.TheMovieDb
                ? new[] { Tmdb, Tvdb }
                : new[] { Tvdb, Tmdb };
        }

        private static IEnumerable<string> GetSources(ManualSeriesSearchSource source)
        {
            if (source == ManualSeriesSearchSource.TheTVDB || source == ManualSeriesSearchSource.Both) yield return Tvdb;
            if (source == ManualSeriesSearchSource.TheMovieDb || source == ManualSeriesSearchSource.Both) yield return Tmdb;
        }

        private static IEnumerable<string> GetSources(AutomaticSeriesSearchSource source)
        {
            if (source == AutomaticSeriesSearchSource.TheTVDB || source == AutomaticSeriesSearchSource.Both) yield return Tvdb;
            if (source == AutomaticSeriesSearchSource.TheMovieDb || source == AutomaticSeriesSearchSource.Both) yield return Tmdb;
        }

        private static int ProviderOrder(IReadOnlyList<string> order, string provider)
        {
            for (var i = 0; i < order.Count; i++)
                if (string.Equals(order[i], provider, StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }

        private static bool IsResolverSource(string provider) =>
            string.Equals(provider, Tvdb, StringComparison.OrdinalIgnoreCase) || string.Equals(provider, Tmdb, StringComparison.OrdinalIgnoreCase);

        private static string NormalizeSource(string provider) =>
            string.Equals(provider, Tvdb, StringComparison.OrdinalIgnoreCase) ? Tvdb : Tmdb;

        private sealed class SourcePlan
        {
            public List<string> Sources { get; set; }
            public List<string> TieBreakOrder { get; set; }
            public bool IncludeDisabledProviders { get; set; }
        }

        private static string GetProviderId(SeriesInfo info, string id)
        {
            string value;
            return info.ProviderIds != null && info.ProviderIds.TryGetValue(id, out value) ? value : null;
        }

        private static RemoteSearchResult FindSearchResult(IEnumerable<RemoteSearchResult> results, string source, SeriesCandidate candidate)
        {
            var providerId = source == Tvdb ? candidate.TvdbId : candidate.TmdbId;
            var providerKey = source == Tvdb ? TvdbId : TmdbId;
            return results.First(r => string.Equals(GetProviderId(r.ProviderIds, providerKey), providerId, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetProviderId(IDictionary<string, string> providerIds, string id)
        {
            string value;
            return providerIds != null && providerIds.TryGetValue(id, out value) ? value : null;
        }

        private static PluginConfiguration GetConfiguration() => Plugin.Instance?.Configuration ?? new PluginConfiguration();

        private sealed class ScoredCandidate
        {
            public SeriesCandidate Candidate { get; set; }
            public RemoteSearchResult SearchResult { get; set; }
        }
    }
}
