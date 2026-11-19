using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;

namespace IdentityHelper.Providers
{
    /// <summary>
    /// Fetches only the Discogs artist profile and preserves visible Markdown link text.
    /// Run this before the Discogs fetcher so Emby's metadata merge keeps this overview.
    /// </summary>
    public sealed class ArtistOverviewProvider : IRemoteMetadataProviderWithOptions<MusicArtist, ArtistInfo>, IHasOrder
    {
        private const string ProviderName = "Identity Helper - Artist";
        private const string DiscogsArtistId = "DiscogsArtist";
        private static readonly TimeSpan PostFetchDelay = TimeSpan.FromSeconds(2);
        private readonly IHttpClient httpClient;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;

        public ArtistOverviewProvider(IHttpClient httpClient, IJsonSerializer json, ILogManager logManager)
        {
            this.httpClient = httpClient;
            this.json = json;
            this.logger = logManager.GetLogger(ProviderName);
        }

        public string Name => ProviderName;

        // Emby processes remote metadata providers in order; run before Discogs.
        public int Order => -100;

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(ArtistInfo searchInfo, CancellationToken cancellationToken)
        {
            // This provider enriches a Discogs match made by Emby; it does not search or select artists.
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());
        }

        public async Task<MetadataResult<MusicArtist>> GetMetadata(ArtistInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<MusicArtist> { Item = new MusicArtist() };
            var discogsId = GetProviderId(info, DiscogsArtistId);
            this.logger.Info("Artist overview fetch started: name={0}, discogsId={1}, providerIds={2}",
                info?.Name ?? "<null>", discogsId ?? "<missing>",
                info?.ProviderIds == null ? 0 : info.ProviderIds.Count);
            if (string.IsNullOrWhiteSpace(discogsId))
            {
                this.logger.Warn("No DiscogsArtist ID was available; returning no metadata.");
                return result;
            }

            if (!Regex.IsMatch(discogsId, @"^\d+$"))
            {
                this.logger.Warn("Invalid DiscogsArtist ID '{0}'; expected digits only.", discogsId);
                return result;
            }

            var request = new HttpRequestOptions
            {
                Url = "https://api.discogs.com/artists/" + discogsId,
                CancellationToken = cancellationToken,
                EnableDefaultUserAgent = true
            };
            request.RequestHeaders["User-Agent"] = "IdentityCheckerArtist/1.0";

            try
            {
                this.logger.Debug("Requesting Discogs artist profile: id={0}, url={1}", discogsId, request.Url);
                using (var response = await this.httpClient.SendAsync(request, "GET").ConfigureAwait(false))
                {
                    this.logger.Info("Discogs response received: id={0}, status={1}, contentLength={2}",
                        discogsId, response.StatusCode, response.ContentLength);
                    this.logger.Info("Discogs rate limit: id={0}, limit={1}, used={2}, remaining={3}",
                        discogsId,
                        GetHeader(response, "X-Discogs-Ratelimit"),
                        GetHeader(response, "X-Discogs-Ratelimit-Used"),
                        GetHeader(response, "X-Discogs-Ratelimit-Remaining"));
                    var artist = await this.json.DeserializeFromStreamAsync<DiscogsArtist>(response.Content).ConfigureAwait(false);
                    if (artist == null)
                    {
                        this.logger.Warn("Discogs response deserialized to null for artist ID {0}.", discogsId);
                        return result;
                    }

                    var profile = artist.Profile;
                    this.logger.Info("Discogs profile parsed: id={0}, responseName={1}, profileNull={2}, profileLength={3}",
                        discogsId, artist.Name ?? "<missing>", profile == null, profile?.Length ?? 0);
                    if (string.IsNullOrWhiteSpace(profile))
                    {
                        this.logger.Warn("Discogs profile was empty for artist ID {0}; returning no metadata.", discogsId);
                        return result;
                    }

                    result.Item.Overview = FlattenMarkdownLinks(profile);
                    result.HasMetadata = !string.IsNullOrWhiteSpace(result.Item.Overview);
                    this.logger.Info("Artist overview result ready: id={0}, overviewLength={1}, hasMetadata={2}",
                        discogsId, result.Item.Overview?.Length ?? 0, result.HasMetadata);
                    if (result.HasMetadata)
                    {
                        this.logger.Debug("Artist overview text for Discogs ID {0}: {1}", discogsId, result.Item.Overview);
                        this.logger.Debug("Waiting {0} ms before returning to Emby so the next Discogs request is spaced out.",
                            (int)PostFetchDelay.TotalMilliseconds);
                        await Task.Delay(PostFetchDelay, cancellationToken).ConfigureAwait(false);
                    }
                    return result;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                this.logger.ErrorException("Artist overview fetch failed for ID " + discogsId, ex);
                // Keep this legacy message searchable when comparing older log excerpts.
                this.logger.ErrorException("Unable to fetch Discogs artist overview for ID " + discogsId, ex);
                return result;
            }
        }

        public Task<MetadataResult<MusicArtist>> GetMetadata(RemoteMetadataFetchOptions<ArtistInfo> options, CancellationToken cancellationToken)
        {
            return GetMetadata(options.SearchInfo, cancellationToken);
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return Task.FromResult<HttpResponseInfo>(null);
        }

        /// <summary>Converts Discogs Markdown links and artist references to visible text.</summary>
        internal static string FlattenMarkdownLinks(string profile)
        {
            if (string.IsNullOrEmpty(profile)) return profile;

            // Discogs profiles use [a=Artist Name] for artist references and Markdown for links.
            // Preserve visible text while removing only the link/reference markup.
            var result = Regex.Replace(profile,
                @"\[a=(?<label>[^\]]+)\]",
                match => match.Groups["label"].Value,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            result = Regex.Replace(result,
                @"\[(?<label>(?:[^\[\]]|\[[^\[\]]*\])*)\]\((?<url>(?:[^()]|\([^()]*\))*)\)",
                match => match.Groups["label"].Value,
                RegexOptions.CultureInvariant);
            return result;
        }

        private static string GetProviderId(ArtistInfo info, string key)
        {
            string value;
            return info != null && info.ProviderIds != null && info.ProviderIds.TryGetValue(key, out value) ? value : null;
        }

        private static string GetHeader(HttpResponseInfo response, string name)
        {
            string value;
            return response?.Headers != null && response.Headers.TryGetValue(name, out value) ? value : "<unavailable>";
        }

        private sealed class DiscogsArtist
        {
            public string name { get; set; }
            public string profile { get; set; }
            public string Name { get { return this.name; } }
            public string Profile { get { return this.profile; } }
        }
    }
}
