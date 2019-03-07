﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using NuGet.Protocol.Catalog;
using NuGet.Services.Entities;

namespace NuGet.Services.AzureSearch
{
    public interface ISearchDocumentBuilder
    {
        SearchDocument.LatestFlags LatestFlagsOrNull(
            VersionLists versionLists,
            SearchFilters searchFilters);

        KeyedDocument Keyed(
            string packageId,
            SearchFilters searchFilters);

        SearchDocument.UpdateVersionList UpdateVersionListFromCatalog(
            string packageId,
            SearchFilters searchFilters,
            DateTimeOffset lastCommitTimestamp,
            string lastCommitId,
            string[] versions,
            bool isLatestStable,
            bool isLatest);

        SearchDocument.Full FullFromDb(
            string packageId, 
            SearchFilters searchFilters,
            string[] versions,
            bool isLatestStable,
            bool isLatest,
            string fullVersion,
            Package package,
            string[] owners,
            long totalDownloadCount);

        // TODO: Add owners
        // https://github.com/nuget/nugetgallery/issues/6475
        SearchDocument.AddFirst AddFirstFromCatalog(
            SearchFilters searchFilters,
            string[] versions,
            bool isLatestStable,
            bool isLatest,
            string normalizedVersion,
            string fullVersion,
            PackageDetailsCatalogLeaf leaf);

        SearchDocument.UpdateLatest UpdateLatestFromCatalog(
            SearchFilters searchFilters,
            string[] versions,
            bool isLatestStable,
            bool isLatest,
            string normalizedVersion,
            string fullVersion,
            PackageDetailsCatalogLeaf leaf);
    }
}