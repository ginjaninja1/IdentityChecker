using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IdentityChecker.Configuration;
using IdentityChecker.Matching;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;

namespace IdentityChecker.Providers
{
    /// <summary>Emby auto-discovers this class from its series metadata provider interface.</summary>
    public sealed class SeriesIdentityResolverProvider : IRemoteMetadataProviderWithOptions<Series, SeriesInfo>, IHasOrder
    {
        private const string ProviderName = "Series Identity Resolver";
        private const string Tvdb = "TheTVDB";
        private const string Tmdb = "TheMovieDb";
        private const string TvdbId = "Tvdb";
        private const string TmdbId = "Tmdb";
        private readonly IProviderManager providerManager;
        private readonly ILogger logger;

        public SeriesIdentityResolverProvider(IProviderManager providerManager, ILogManager logManager)
        {
            this.providerManager = providerManager;
            this.logger = logManager.GetLogger(ProviderName);
        }

        public string Name => ProviderName;
        public int Order => 0;

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return Task.FromResult<HttpResponseInfo>(null);
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            var config = GetConfiguration();
            if (!config.EnablePlugin) return Enumerable.Empty<RemoteSearchResult>();
            var correlation = Guid.NewGuid().ToString("N").Substring(0, 8);
            this.logger.Info("ResolverStarted {0}: pathway=manual-search, name={1}, year={2}, source={3}, language={4}, country={5}",
                correlation, searchInfo.Name, searchInfo.Year, config.SourceMode, searchInfo.MetadataLanguage, searchInfo.MetadataCountryCode);
            var found = await Search(searchInfo, null, config, cancellationToken).ConfigureAwait(false);
            var selection = SeriesCandidateSelector.Select(searchInfo.Name, searchInfo.Year, found.Select(x => x.Candidate), config, Tvdb);
            LogSelection(correlation, selection, config);
            this.logger.Info("ResolverDecision {0}: manual-search returned={1}, selected={2}, reason={3}",
                correlation, selection.Ranked.Count, selection.Selected?.Candidate.Name ?? "none", selection.Reason);
            return selection.Ranked.Select(x => ToSearchResult(x.Candidate)).ToList();
        }

        public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            return await Resolve(info, null, cancellationToken).ConfigureAwait(false);
        }

        public async Task<MetadataResult<Series>> GetMetadata(RemoteMetadataFetchOptions<SeriesInfo> options, CancellationToken cancellationToken)
        {
            return await Resolve(options.SearchInfo, null, cancellationToken).ConfigureAwait(false);
        }

        private async Task<MetadataResult<Series>> Resolve(SeriesInfo info, BaseItem referenceItem, CancellationToken cancellationToken)
        {
            var config = GetConfiguration();
            var result = new MetadataResult<Series> { Item = new Series(), HasMetadata = true };
            var correlation = Guid.NewGuid().ToString("N").Substring(0, 8);
            if (!config.EnablePlugin) { this.logger.Info("ResolverStarted {0}: disabled", correlation); return result; }
            var tvdbExisting = GetProviderId(info, TvdbId);
            var tmdbExisting = GetProviderId(info, TmdbId);
            this.logger.Info("ResolverStarted {0}: name={1}, year={2}, tvdb={3}, tmdb={4}, source={5}, apply={6}, language={7}, country={8}",
                correlation, info.Name, info.Year, tvdbExisting, tmdbExisting, config.SourceMode, config.ApplySelectedIds,
                info.MetadataLanguage, info.MetadataCountryCode);
            if (!string.IsNullOrWhiteSpace(tvdbExisting) || !string.IsNullOrWhiteSpace(tmdbExisting))
            {
                this.logger.Info("ResolverDecision {0}: preserve existing provider ID", correlation);
                return result;
            }

            var found = await Search(info, referenceItem, config, cancellationToken).ConfigureAwait(false);
            var selection = SeriesCandidateSelector.Select(info.Name, info.Year, found.Select(x => x.Candidate), config, Tvdb);
            LogSelection(correlation, selection, config);
            var chosen = selection.Selected;
            if (chosen == null)
            {
                this.logger.Info("ResolverDecision {0}: abstain ({1}), topScore={2:F3}, margin={3:F3}", correlation, selection.Reason,
                    selection.Ranked.FirstOrDefault()?.TotalScore ?? 0, selection.Margin);
                return result;
            }
            this.logger.Info("ResolverDecision {0}: candidate={1}, source={2}, confidence={3:F3}, margin={4:F3}, reason={5}, apply={6}",
                correlation, chosen.Candidate.Name, chosen.Candidate.Source, chosen.TotalScore, selection.Margin, selection.Reason, config.ApplySelectedIds);
            if (!config.ApplySelectedIds) return result;
            if (!string.IsNullOrWhiteSpace(chosen.Candidate.TvdbId)) result.Item.SetProviderId(TvdbId, chosen.Candidate.TvdbId);
            if (!string.IsNullOrWhiteSpace(chosen.Candidate.TmdbId)) result.Item.SetProviderId(TmdbId, chosen.Candidate.TmdbId);
            this.logger.Info("IdSeeded {0}: tvdb={1}, tmdb={2}; stock metadata fetchers will process the item next", correlation,
                chosen.Candidate.TvdbId, chosen.Candidate.TmdbId);
            return result;
        }

        private void LogSelection(string correlation, SeriesSelection selection, PluginConfiguration config)
        {
            foreach (var s in selection.Ranked)
            {
                this.logger.Info("CandidateScore {0}: provider={1}, title={2}, normalized={3}/{4}, distance={5}, titleScore={6:F3}, yearDelta={7}, yearScore={8:F3}, total={9:F3}",
                    correlation, s.Candidate.Source, s.Candidate.Name, s.NormalizedInput, s.NormalizedCandidate, s.EditDistance,
                    s.TitleSimilarity, s.YearDelta, s.YearScore, s.TotalScore);
                if (config.VerboseCandidateLogging) this.logger.Info("Candidate {0}: source={1}, position={2}, title={3}, year={4}, tvdb={5}, tmdb={6}",
                    correlation, s.Candidate.Source, s.Candidate.Position, s.Candidate.Name, s.Candidate.Year, s.Candidate.TvdbId, s.Candidate.TmdbId);
            }
            if (selection.Selected == null)
            {
                this.logger.Info("ResolverSelection {0}: abstain ({1}), topScore={2:F3}, margin={3:F3}", correlation, selection.Reason,
                    selection.Ranked.FirstOrDefault()?.TotalScore ?? 0, selection.Margin);
                return;
            }
            this.logger.Info("ResolverSelection {0}: candidate={1}, source={2}, score={3:F3}, margin={4:F3}, reason={5}",
                correlation, selection.Selected.Candidate.Name, selection.Selected.Candidate.Source,
                selection.Selected.TotalScore, selection.Margin, selection.Reason);
        }

        private async Task<List<ScoredCandidate>> Search(SeriesInfo info, BaseItem referenceItem, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var output = new List<ScoredCandidate>();
            foreach (var source in GetSources(config.SourceMode))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var query = new RemoteSearchQuery<SeriesInfo> {
                    SearchInfo = info,
                    SearchProviderName = source,
                    IncludeDisabledProviders = false
                };
                var watch = Stopwatch.StartNew();
                this.logger.Info("SearchStarted: provider={0}, name={1}, year={2}, language={3}, country={4}", source,
                    info.Name, info.Year, info.MetadataLanguage, info.MetadataCountryCode);
                try
                {
                    // SDK 4.9 exposes only the query/token overload. The 4.11 reference-item overload is unavailable here.
                    var results = (await this.providerManager.GetRemoteSearchResults<Series, SeriesInfo>(query, cancellationToken).ConfigureAwait(false)
                        ?? Enumerable.Empty<RemoteSearchResult>()).ToList();
                    this.logger.Info("SearchCompleted: provider={0}, status=ok, count={1}, durationMs={2}", source, results.Count, watch.ElapsedMilliseconds);
                    var position = 0;
                    var sourceCandidates = new List<SeriesCandidate>();
                    foreach (var item in results)
                    {
                        position++;
                        var ids = item.ProviderIds ?? new Dictionary<string, string>();
                        var candidate = new SeriesCandidate {
                            Name = item.Name, Year = item.ProductionYear, Source = source, Position = position,
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
                            output.Add(new ScoredCandidate { Candidate = combined });
                        }
                        else output.AddRange(sourceCandidates.Select(c => new ScoredCandidate { Candidate = c }));
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

        private static IEnumerable<string> GetSources(SeriesSearchSourceMode mode)
        {
            if (mode == SeriesSearchSourceMode.Tvdb || mode == SeriesSearchSourceMode.Both) yield return Tvdb;
            if (mode == SeriesSearchSourceMode.Tmdb || mode == SeriesSearchSourceMode.Both) yield return Tmdb;
        }

        private static RemoteSearchResult ToSearchResult(SeriesCandidate c)
        {
            var result = new RemoteSearchResult { Name = c.Name, ProductionYear = c.Year, SearchProviderName = c.Source };
            if (!string.IsNullOrWhiteSpace(c.TvdbId)) result.SetProviderId(TvdbId, c.TvdbId);
            if (!string.IsNullOrWhiteSpace(c.TmdbId)) result.SetProviderId(TmdbId, c.TmdbId);
            return result;
        }

        private static string GetProviderId(SeriesInfo info, string id)
        {
            string value;
            return info.ProviderIds != null && info.ProviderIds.TryGetValue(id, out value) ? value : null;
        }

        private static PluginConfiguration GetConfiguration() => Plugin.Instance?.Configuration ?? new PluginConfiguration();

        private sealed class ScoredCandidate { public SeriesCandidate Candidate { get; set; } public SeriesCandidateScore Score { get; set; } }
    }
}
