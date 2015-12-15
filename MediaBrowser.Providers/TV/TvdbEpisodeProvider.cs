﻿using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using CommonIO;

namespace MediaBrowser.Providers.TV
{

    /// <summary>
    /// Class RemoteEpisodeProvider
    /// </summary>
    class TvdbEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IItemIdentityProvider<EpisodeInfo>, IHasChangeMonitor
    {
        private static readonly string FullIdKey = MetadataProviders.Tvdb + "-Full";

        internal static TvdbEpisodeProvider Current;
        private readonly IFileSystem _fileSystem;
        private readonly IServerConfigurationManager _config;
        private readonly IHttpClient _httpClient;
        private readonly ILogger _logger;

        public TvdbEpisodeProvider(IFileSystem fileSystem, IServerConfigurationManager config, IHttpClient httpClient, ILogger logger)
        {
            _fileSystem = fileSystem;
            _config = config;
            _httpClient = httpClient;
            _logger = logger;
            Current = this;
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            var list = new List<RemoteSearchResult>();

            if (TvdbSeriesProvider.IsValidSeries(searchInfo.SeriesProviderIds) && searchInfo.IndexNumber.HasValue)
            {
                var seriesDataPath = TvdbSeriesProvider.GetSeriesDataPath(_config.ApplicationPaths, searchInfo.SeriesProviderIds);

                var searchNumbers = new EpisodeNumbers();
                searchNumbers.EpisodeNumber = searchInfo.IndexNumber.Value;
                searchNumbers.SeasonNumber = searchInfo.ParentIndexNumber;
                searchNumbers.EpisodeNumberEnd = searchInfo.IndexNumberEnd ?? searchNumbers.EpisodeNumber; 

                try
                {
                    var metadataResult = FetchEpisodeData(searchInfo, searchNumbers, seriesDataPath, cancellationToken);

                    if (metadataResult.HasMetadata)
                    {
                        var item = metadataResult.Item;

                        list.Add(new RemoteSearchResult
                        {
                            IndexNumber = item.IndexNumber,
                            Name = item.Name,
                            ParentIndexNumber = item.ParentIndexNumber,
                            PremiereDate = item.PremiereDate,
                            ProductionYear = item.ProductionYear,
                            ProviderIds = item.ProviderIds,
                            SearchProviderName = Name,
                            IndexNumberEnd = item.IndexNumberEnd
                        });
                    }
                }
                catch (FileNotFoundException)
                {
                    // Don't fail the provider because this will just keep on going and going.
                }
                catch (DirectoryNotFoundException)
                {
                    // Don't fail the provider because this will just keep on going and going.
                }
            }

            return list;
        }

        public string Name
        {
            get { return "TheTVDB"; }
        }

        public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            var identity = Identity.ParseIdentity(searchInfo.GetProviderId(FullIdKey));

            if (identity == null)
            {
                await Identify(searchInfo).ConfigureAwait(false);
                identity = Identity.ParseIdentity(searchInfo.GetProviderId(FullIdKey));
            }

            var result = new MetadataResult<Episode>();

            if (identity != null)
            {
                var seriesProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                seriesProviderIds[MetadataProviders.Tvdb.ToString()] = identity.Value.SeriesId;
                var seriesDataPath = TvdbSeriesProvider.GetSeriesDataPath(_config.ApplicationPaths, seriesProviderIds);

                var searchNumbers = new EpisodeNumbers();
                searchNumbers.EpisodeNumber = identity.Value.EpisodeNumber;
                var seasonOffset = TvdbSeriesProvider.GetSeriesOffset(searchInfo.SeriesProviderIds) ?? 0;
                searchNumbers.SeasonNumber = identity.Value.SeasonIndex + seasonOffset;
                searchNumbers.EpisodeNumberEnd = identity.Value.EpisodeNumberEnd ?? searchNumbers.EpisodeNumber;
                
                try
                {
                    result = FetchEpisodeData(searchInfo, searchNumbers, seriesDataPath, cancellationToken);
                }
                catch (FileNotFoundException)
                {
                    // Don't fail the provider because this will just keep on going and going.
                }
                catch (DirectoryNotFoundException)
                {
                    // Don't fail the provider because this will just keep on going and going.
                }
            }
            else if (TvdbSeriesProvider.IsValidSeries(searchInfo.SeriesProviderIds) && searchInfo.IndexNumber.HasValue)
            {
                var seriesDataPath = TvdbSeriesProvider.GetSeriesDataPath(_config.ApplicationPaths, searchInfo.SeriesProviderIds);

                var searchNumbers = new EpisodeNumbers();
                searchNumbers.EpisodeNumber = searchInfo.IndexNumber.Value;
                searchNumbers.SeasonNumber = searchInfo.ParentIndexNumber;
                searchNumbers.EpisodeNumberEnd = searchInfo.IndexNumberEnd ?? searchNumbers.EpisodeNumber; 

                try
                {
                    result = FetchEpisodeData(searchInfo, searchNumbers, seriesDataPath, cancellationToken);
                }
                catch (FileNotFoundException)
                {
                    // Don't fail the provider because this will just keep on going and going.
                }
                catch (DirectoryNotFoundException)
                {
                    // Don't fail the provider because this will just keep on going and going.
                }
            }
            else
            {
                _logger.Debug("No series identity found for {0}", searchInfo.Name);
            }

            return result;
        }

        public bool HasChanged(IHasMetadata item, IDirectoryService directoryService, DateTime date)
        {
            // Only enable for virtual items
            if (item.LocationType != LocationType.Virtual)
            {
                return false;
            }

            var episode = (Episode)item;
            var series = episode.Series;

            if (series != null && TvdbSeriesProvider.IsValidSeries(series.ProviderIds))
            {
                // Process images
                var seriesDataPath = TvdbSeriesProvider.GetSeriesDataPath(_config.ApplicationPaths, series.ProviderIds);

                var files = GetEpisodeXmlFiles(episode.ParentIndexNumber, episode.IndexNumber, episode.IndexNumberEnd, seriesDataPath);

                return files.Any(i => _fileSystem.GetLastWriteTimeUtc(i) > date);
            }

            return false;
        }

        /// <summary>
        /// Gets the episode XML files.
        /// </summary>
        /// <param name="seasonNumber">The season number.</param>
        /// <param name="episodeNumber">The episode number.</param>
        /// <param name="endingEpisodeNumber">The ending episode number.</param>
        /// <param name="seriesDataPath">The series data path.</param>
        /// <returns>List{FileInfo}.</returns>
        internal List<FileSystemMetadata> GetEpisodeXmlFiles(int? seasonNumber, int? episodeNumber, int? endingEpisodeNumber, string seriesDataPath)
        {
            var files = new List<FileSystemMetadata>();

            if (episodeNumber == null)
            {
                return files;
            }

            if (seasonNumber == null)
            {
                return files;
            }

            var file = Path.Combine(seriesDataPath, string.Format("episode-{0}-{1}.xml", seasonNumber.Value, episodeNumber));

            var fileInfo = _fileSystem.GetFileInfo(file);
            var usingAbsoluteData = false;

            if (fileInfo.Exists)
            {
                files.Add(fileInfo);
            }
            else
            {
                file = Path.Combine(seriesDataPath, string.Format("episode-abs-{0}.xml", episodeNumber));
                fileInfo = _fileSystem.GetFileInfo(file);
                if (fileInfo.Exists)
                {
                    files.Add(fileInfo);
                    usingAbsoluteData = true;
                }
            }

            var end = endingEpisodeNumber ?? episodeNumber;
            episodeNumber++;

            while (episodeNumber <= end)
            {
                if (usingAbsoluteData)
                {
                    file = Path.Combine(seriesDataPath, string.Format("episode-abs-{0}.xml", episodeNumber));
                }
                else
                {
                    file = Path.Combine(seriesDataPath, string.Format("episode-{0}-{1}.xml", seasonNumber.Value, episodeNumber));
                }

                fileInfo = _fileSystem.GetFileInfo(file);
                if (fileInfo.Exists)
                {
                    files.Add(fileInfo);
                }
                else
                {
                    break;
                }

                episodeNumber++;
            }

            return files;
        }

        private class EpisodeNumbers
        {
            public int EpisodeNumber;
            public int? SeasonNumber;
            public int EpisodeNumberEnd;
        }

        /// <summary>
        /// Fetches the episode data.
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <param name="searchNumbers">The search numbers.</param>
        /// <param name="seriesDataPath">The series data path.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{System.Boolean}.</returns>
        private MetadataResult<Episode> FetchEpisodeData(EpisodeInfo id, EpisodeNumbers searchNumbers, string seriesDataPath, CancellationToken cancellationToken)
        {
            var episodeNumber = searchNumbers.EpisodeNumber;
            var seasonNumber = searchNumbers.SeasonNumber;
            
            string file;
            var usingAbsoluteData = false;

            var result = new MetadataResult<Episode>()
            {
                Item = new Episode
                {
                    IndexNumber = id.IndexNumber,
                    ParentIndexNumber = id.ParentIndexNumber,
                    IndexNumberEnd = id.IndexNumberEnd
                }
            };

            try
            {
                if (seasonNumber != null)
                {
                    file = Path.Combine(seriesDataPath, string.Format("episode-{0}-{1}.xml", seasonNumber.Value, episodeNumber));
                    FetchMainEpisodeInfo(result, file, cancellationToken);

                    result.HasMetadata = true;
                }
            }
            catch (FileNotFoundException)
            {
                // Could be using absolute numbering
                if (seasonNumber.HasValue && seasonNumber.Value != 1)
                {
                    throw;
                }
            }

            if (!result.HasMetadata)
            {
                file = Path.Combine(seriesDataPath, string.Format("episode-abs-{0}.xml", episodeNumber));

                FetchMainEpisodeInfo(result, file, cancellationToken);
                result.HasMetadata = true;
                usingAbsoluteData = true;
            }

            var end = searchNumbers.EpisodeNumberEnd;
            episodeNumber++;

            while (episodeNumber <= end)
            {
                if (usingAbsoluteData)
                {
                    file = Path.Combine(seriesDataPath, string.Format("episode-abs-{0}.xml", episodeNumber));
                }
                else
                {
                    file = Path.Combine(seriesDataPath, string.Format("episode-{0}-{1}.xml", seasonNumber.Value, episodeNumber));
                }

                try
                {
                    FetchAdditionalPartInfo(result, file, cancellationToken);
                }
                catch (FileNotFoundException)
                {
                    break;
                }
                catch (DirectoryNotFoundException)
                {
                    break;
                }

                episodeNumber++;
            }

            return result;
        }

        private readonly CultureInfo _usCulture = new CultureInfo("en-US");

        private void FetchMainEpisodeInfo(MetadataResult<Episode> result, string xmlFile, CancellationToken cancellationToken)
        {
            var item = result.Item;

            using (var streamReader = new StreamReader(xmlFile, Encoding.UTF8))
            {
                // Use XmlReader for best performance
                using (var reader = XmlReader.Create(streamReader, new XmlReaderSettings
                {
                    CheckCharacters = false,
                    IgnoreProcessingInstructions = true,
                    IgnoreComments = true,
                    ValidationType = ValidationType.None
                }))
                {
                    reader.MoveToContent();

                    result.ResetPeople();

                    // Loop through each element
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            switch (reader.Name)
                            {
                                case "id":
                                    {
                                        var val = reader.ReadElementContentAsString();
                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            item.SetProviderId(MetadataProviders.Tvdb, val);
                                        }
                                        break;
                                    }

                                case "IMDB_ID":
                                    {
                                        var val = reader.ReadElementContentAsString();
                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            item.SetProviderId(MetadataProviders.Imdb, val);
                                        }
                                        break;
                                    }

                                case "DVD_episodenumber":
                                    {
                                        var val = reader.ReadElementContentAsString();

                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            float num;

                                            if (float.TryParse(val, NumberStyles.Any, _usCulture, out num))
                                            {
                                                item.DvdEpisodeNumber = num;
                                            }
                                        }

                                        break;
                                    }

                                case "DVD_season":
                                    {
                                        var val = reader.ReadElementContentAsString();

                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            float num;

                                            if (float.TryParse(val, NumberStyles.Any, _usCulture, out num))
                                            {
                                                item.DvdSeasonNumber = Convert.ToInt32(num);
                                            }
                                        }

                                        break;
                                    }

                                case "absolute_number":
                                    {
                                        var val = reader.ReadElementContentAsString();

                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            int rval;

                                            // int.TryParse is local aware, so it can be probamatic, force us culture
                                            if (int.TryParse(val, NumberStyles.Integer, _usCulture, out rval))
                                            {
                                                item.AbsoluteEpisodeNumber = rval;
                                            }
                                        }

                                        break;
                                    }

                                case "airsbefore_episode":
                                    {
                                        var val = reader.ReadElementContentAsString();

                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            int rval;

                                            // int.TryParse is local aware, so it can be probamatic, force us culture
                                            if (int.TryParse(val, NumberStyles.Integer, _usCulture, out rval))
                                            {
                                                item.AirsBeforeEpisodeNumber = rval;
                                            }
                                        }

                                        break;
                                    }

                                case "airsafter_season":
                                    {
                                        var val = reader.ReadElementContentAsString();

                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            int rval;

                                            // int.TryParse is local aware, so it can be probamatic, force us culture
                                            if (int.TryParse(val, NumberStyles.Integer, _usCulture, out rval))
                                            {
                                                item.AirsAfterSeasonNumber = rval;
                                            }
                                        }

                                        break;
                                    }

                                case "airsbefore_season":
                                    {
                                        var val = reader.ReadElementContentAsString();

                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            int rval;

                                            // int.TryParse is local aware, so it can be probamatic, force us culture
                                            if (int.TryParse(val, NumberStyles.Integer, _usCulture, out rval))
                                            {
                                                item.AirsBeforeSeasonNumber = rval;
                                            }
                                        }

                                        break;
                                    }

                                case "EpisodeName":
                                    {
                                        if (!item.LockedFields.Contains(MetadataFields.Name))
                                        {
                                            var val = reader.ReadElementContentAsString();
                                            if (!string.IsNullOrWhiteSpace(val))
                                            {
                                                item.Name = val;
                                            }
                                        }
                                        break;
                                    }

                                case "Overview":
                                    {
                                        if (!item.LockedFields.Contains(MetadataFields.Overview))
                                        {
                                            var val = reader.ReadElementContentAsString();
                                            if (!string.IsNullOrWhiteSpace(val))
                                            {
                                                item.Overview = val;
                                            }
                                        }
                                        break;
                                    }
                                case "Rating":
                                    {
                                        var val = reader.ReadElementContentAsString();

                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            float rval;

                                            // float.TryParse is local aware, so it can be probamatic, force us culture
                                            if (float.TryParse(val, NumberStyles.AllowDecimalPoint, _usCulture, out rval))
                                            {
                                                item.CommunityRating = rval;
                                            }
                                        }
                                        break;
                                    }
                                case "RatingCount":
                                    {
                                        var val = reader.ReadElementContentAsString();

                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            int rval;

                                            // int.TryParse is local aware, so it can be probamatic, force us culture
                                            if (int.TryParse(val, NumberStyles.Integer, _usCulture, out rval))
                                            {
                                                item.VoteCount = rval;
                                            }
                                        }

                                        break;
                                    }

                                case "FirstAired":
                                    {
                                        var val = reader.ReadElementContentAsString();

                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            DateTime date;
                                            if (DateTime.TryParse(val, out date))
                                            {
                                                date = date.ToUniversalTime();

                                                item.PremiereDate = date;
                                                item.ProductionYear = date.Year;
                                            }
                                        }

                                        break;
                                    }

                                case "Director":
                                    {
                                        var val = reader.ReadElementContentAsString();

                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            if (!item.LockedFields.Contains(MetadataFields.Cast))
                                            {
                                                AddPeople(result, val, PersonType.Director);
                                            }
                                        }

                                        break;
                                    }
                                case "GuestStars":
                                    {
                                        var val = reader.ReadElementContentAsString();

                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            if (!item.LockedFields.Contains(MetadataFields.Cast))
                                            {
                                                AddGuestStars(result, val);
                                            }
                                        }

                                        break;
                                    }
                                case "Writer":
                                    {
                                        var val = reader.ReadElementContentAsString();

                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            if (!item.LockedFields.Contains(MetadataFields.Cast))
                                            {
                                                AddPeople(result, val, PersonType.Writer);
                                            }
                                        }

                                        break;
                                    }

                                default:
                                    reader.Skip();
                                    break;
                            }
                        }
                    }
                }
            }
        }

        private void AddPeople<T>(MetadataResult<T> result, string val, string personType)
        {
            // Sometimes tvdb actors have leading spaces
            foreach (var person in val.Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                            .Where(i => !string.IsNullOrWhiteSpace(i))
                                            .Select(str => new PersonInfo { Type = personType, Name = str.Trim() }))
            {
                result.AddPerson(person);
            }
        }

        private void AddGuestStars<T>(MetadataResult<T> result, string val)
            where T : BaseItem
        {
            // Sometimes tvdb actors have leading spaces
            //Regex Info:
            //The first block are the posible delimitators (open-parentheses should be there cause if dont the next block will fail)
            //The second block Allow the delimitators to be part of the text if they're inside parentheses
            var persons = Regex.Matches(val, @"(?<delimitators>([^|,(])|(?<ignoreinParentheses>\([^)]*\)*))+")
                .Cast<Match>()
                .Select(m => m.Value)
                .Where(i => !string.IsNullOrWhiteSpace(i) && !string.IsNullOrEmpty(i));

            foreach (var person in persons.Select(str =>
            {
                var nameGroup = str.Split(new[] { '(' }, 2, StringSplitOptions.RemoveEmptyEntries);
                var name = nameGroup[0].Trim();
                var roles = nameGroup.Count() > 1 ? nameGroup[1].Trim() : null;
                if (roles != null)
                    roles = roles.EndsWith(")") ? roles.Substring(0, roles.Length - 1) : roles;

                return new PersonInfo { Type = PersonType.GuestStar, Name = name, Role = roles };
            }))
            {
                if (!string.IsNullOrWhiteSpace(person.Name))
                {
                    result.AddPerson(person);
                }
            }
        }

        private void FetchAdditionalPartInfo(MetadataResult<Episode> result, string xmlFile, CancellationToken cancellationToken)
        {
            var item = result.Item;

            using (var streamReader = new StreamReader(xmlFile, Encoding.UTF8))
            {
                // Use XmlReader for best performance
                using (var reader = XmlReader.Create(streamReader, new XmlReaderSettings
                {
                    CheckCharacters = false,
                    IgnoreProcessingInstructions = true,
                    IgnoreComments = true,
                    ValidationType = ValidationType.None
                }))
                {
                    reader.MoveToContent();

                    // Loop through each element
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            switch (reader.Name)
                            {
                                case "EpisodeName":
                                    {
                                        if (!item.LockedFields.Contains(MetadataFields.Name))
                                        {
                                            var val = reader.ReadElementContentAsString();
                                            if (!string.IsNullOrWhiteSpace(val))
                                            {
                                                item.Name += ", " + val;
                                            }
                                        }
                                        break;
                                    }

                                case "Overview":
                                    {
                                        if (!item.LockedFields.Contains(MetadataFields.Overview))
                                        {
                                            var val = reader.ReadElementContentAsString();
                                            if (!string.IsNullOrWhiteSpace(val))
                                            {
                                                item.Overview += Environment.NewLine + Environment.NewLine + val;
                                            }
                                        }
                                        break;
                                    }
                                case "Director":
                                    {
                                        var val = reader.ReadElementContentAsString();

                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            if (!item.LockedFields.Contains(MetadataFields.Cast))
                                            {
                                                AddPeople(result, val, PersonType.Director);
                                            }
                                        }

                                        break;
                                    }
                                case "GuestStars":
                                    {
                                        var val = reader.ReadElementContentAsString();

                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            if (!item.LockedFields.Contains(MetadataFields.Cast))
                                            {
                                                AddGuestStars(result, val);
                                            }
                                        }

                                        break;
                                    }
                                case "Writer":
                                    {
                                        var val = reader.ReadElementContentAsString();

                                        if (!string.IsNullOrWhiteSpace(val))
                                        {
                                            if (!item.LockedFields.Contains(MetadataFields.Cast))
                                            {
                                                AddPeople(result, val, PersonType.Writer);
                                            }
                                        }

                                        break;
                                    }

                                default:
                                    reader.Skip();
                                    break;
                            }
                        }
                    }
                }
            }
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClient.GetResponse(new HttpRequestOptions
            {
                CancellationToken = cancellationToken,
                Url = url,
                ResourcePool = TvdbSeriesProvider.Current.TvDbResourcePool
            });
        }

        public Task Identify(EpisodeInfo info)
        {
            if (info.ProviderIds.ContainsKey(FullIdKey))
            {
                return Task.FromResult<object>(null);
            }

            string seriesTvdbId;
            info.SeriesProviderIds.TryGetValue(MetadataProviders.Tvdb.ToString(), out seriesTvdbId);

            if (string.IsNullOrEmpty(seriesTvdbId) || info.IndexNumber == null)
            {
                return Task.FromResult<object>(null);
            }

            var id = new Identity(seriesTvdbId, info.ParentIndexNumber, info.IndexNumber.Value, info.IndexNumberEnd);
            info.SetProviderId(FullIdKey, id.ToString());

            return Task.FromResult(id);
        }

        public int Order { get { return 0; } }

        public struct Identity
        {
            public string SeriesId { get; private set; }
            public int? SeasonIndex { get; private set; }
            public int EpisodeNumber { get; private set; }
            public int? EpisodeNumberEnd { get; private set; }

            public Identity(string id)
                : this()
            {
                this = ParseIdentity(id).Value;
            }

            public Identity(string seriesId, int? seasonIndex, int episodeNumber, int? episodeNumberEnd)
                : this()
            {
                SeriesId = seriesId;
                SeasonIndex = seasonIndex;
                EpisodeNumber = episodeNumber;
                EpisodeNumberEnd = episodeNumberEnd;
            }

            public override string ToString()
            {
                return string.Format("{0}:{1}:{2}",
                    SeriesId,
                    SeasonIndex != null ? SeasonIndex.Value.ToString() : "A",
                    EpisodeNumber + (EpisodeNumberEnd != null ? "-" + EpisodeNumberEnd.Value.ToString() : ""));
            }

            public static Identity? ParseIdentity(string id)
            {
                if (string.IsNullOrEmpty(id))
                    return null;

                try {
                    var parts = id.Split(':');
                    var series = parts[0];
                    var season = parts[1] != "A" ? (int?)int.Parse(parts[1]) : null;

                    int index;
                    int? indexEnd;

                    if (parts[2].Contains("-")) {
                        var split = parts[2].IndexOf("-", StringComparison.OrdinalIgnoreCase);
                        index = int.Parse(parts[2].Substring(0, split));
                        indexEnd = int.Parse(parts[2].Substring(split + 1));
                    } else {
                        index = int.Parse(parts[2]);
                        indexEnd = null;
                    }

                    return new Identity(series, season, index, indexEnd);
                } catch {
                    return null;
                }
            }
        }
    }
}
