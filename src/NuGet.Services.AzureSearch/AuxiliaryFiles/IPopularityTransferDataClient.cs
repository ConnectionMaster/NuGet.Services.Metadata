﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Threading.Tasks;
using NuGetGallery;

namespace NuGet.Services.AzureSearch.AuxiliaryFiles
{
    /// <summary>
    /// The purpose of this interface is allow reading and writing populairty transfer information from storage.
    /// The Auxiliary2AzureSearch job does a comparison of latest popularity transfer data from the database with
    /// a snapshot of information stored in Azure Blob Storage. This interface handles the reading and writing of
    /// that snapshot from storage.
    /// </summary>
    public interface IPopularityTransferDataClient
    {
        /// <summary>
        /// Read all of the latest indexed popularity transfers from storage. Also, return the current etag to allow
        /// optimistic concurrency checks on the writing of the file. The returned dictionary's key is the
        /// package ID that is transferring away its popularity, and the values are the package IDs receiving popularity.
        /// The dictionary and the sets are case-insensitive.
        /// </summary>
        Task<ResultAndAccessCondition<SortedDictionary<string, SortedSet<string>>>> ReadLatestIndexedAsync();

        /// <summary>
        /// Replace the existing latest indexed popularity transfers file (i.e. "popularityTransfers.v1.json" file).
        /// </summary>
        /// <param name="newData">The new data to be serialized into storage.</param>
        /// <param name="accessCondition">The access condition (i.e. etag) to use during the upload.</param>
        Task ReplaceLatestIndexedAsync(
            SortedDictionary<string, SortedSet<string>> newData,
            IAccessCondition accessCondition);
    }
}

