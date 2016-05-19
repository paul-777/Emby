﻿using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Providers.Manager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommonIO;

namespace MediaBrowser.Providers.TV
{
    public class SeasonMetadataService : MetadataService<Season, SeasonInfo>
    {
        public SeasonMetadataService(IServerConfigurationManager serverConfigurationManager, ILogger logger, IProviderManager providerManager, IProviderRepository providerRepo, IFileSystem fileSystem, IUserDataManager userDataManager, ILibraryManager libraryManager) : base(serverConfigurationManager, logger, providerManager, providerRepo, fileSystem, userDataManager, libraryManager)
        {
        }

        protected override async Task<ItemUpdateType> BeforeSave(Season item, bool isFullRefresh, ItemUpdateType currentUpdateType)
        {
            var updateType = await base.BeforeSave(item, isFullRefresh, currentUpdateType).ConfigureAwait(false);

            if (item.IndexNumber.HasValue && item.IndexNumber.Value == 0)
            {
                if (!string.Equals(item.Name, ServerConfigurationManager.Configuration.SeasonZeroDisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    item.Name = ServerConfigurationManager.Configuration.SeasonZeroDisplayName;
                    updateType = updateType | ItemUpdateType.MetadataEdit;
                }
            }

            if (isFullRefresh || currentUpdateType > ItemUpdateType.None)
            {
                var episodes = item.GetEpisodes().ToList();
                updateType |= SavePremiereDate(item, episodes);
                updateType |= SaveIsMissing(item, episodes);
            }

            return updateType;
        }

        protected override void MergeData(MetadataResult<Season> source, MetadataResult<Season> target, List<MetadataFields> lockedFields, bool replaceData, bool mergeMetadataSettings)
        {
            ProviderUtils.MergeBaseItemData(source, target, lockedFields, replaceData, mergeMetadataSettings);
        }

        private ItemUpdateType SavePremiereDate(Season item, List<Episode> episodes)
        {
            var dates = episodes.Where(i => i.PremiereDate.HasValue).Select(i => i.PremiereDate.Value).ToList();

            DateTime? premiereDate = null;

            if (dates.Count > 0)
            {
                premiereDate = dates.Min();
            }

            if (item.PremiereDate != premiereDate)
            {
                item.PremiereDate = premiereDate;
                return ItemUpdateType.MetadataEdit;
            }

            return ItemUpdateType.None;
        }

        private ItemUpdateType SaveIsMissing(Season item, List<Episode> episodes)
        {
            var isMissing = item.LocationType == LocationType.Virtual && episodes.All(i => i.IsMissingEpisode);

            if (item.IsMissingSeason != isMissing)
            {
                item.IsMissingSeason = isMissing;
                return ItemUpdateType.MetadataEdit;
            }

            return ItemUpdateType.None;
        }
    }
}
