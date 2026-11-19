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
        private const string ProviderName = "Identity Checker - Artist";
        private const string DiscogsArtistId = "DiscogsArtist";
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
            if (string.IsNullOrWhiteSpace(discogsId)) return result;

            if (!Regex.IsMatch(discogsId, @"^\d+$"))
            {
                this.logger.Warn("Skipping invalid Discogs artist ID: {0}", discogsId);
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
                using (var response = await this.httpClient.SendAsync(request, "GET").ConfigureAwait(false))
                {
                    var artist = await this.json.DeserializeFromStreamAsync<DiscogsArtist>(response.Content).ConfigureAwait(false);
                    if (artist == null || string.IsNullOrWhiteSpace(artist.Profile)) return result;

                    result.Item.Overview = FlattenMarkdownLinks(artist.Profile);
                    result.HasMetadata = !string.IsNullOrWhiteSpace(result.Item.Overview);
                    if (result.HasMetadata)
                        this.logger.Info("Fetched Discogs overview for artist ID {0}", discogsId);
                    return result;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
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

        /// <summary>Changes [display text](URL) to display text; retains all other profile text.</summary>
        internal static string FlattenMarkdownLinks(string profile)
        {
            if (string.IsNullOrEmpty(profile)) return profile;

            // Match balanced brackets/parentheses for common Markdown link text and URLs.
            // The URL and Markdown delimiters are removed; visible label text is preserved.
            var result = Regex.Replace(profile,
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

        private sealed class DiscogsArtist
        {
            public string profile { get; set; }
            public string Profile { get { return this.profile; } }
        }
    }
}
