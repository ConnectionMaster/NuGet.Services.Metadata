﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Store.Azure;
using Microsoft.WindowsAzure.Storage;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using FrameworkLogger = Microsoft.Extensions.Logging.ILogger;

namespace NuGet.Indexing
{
    public class NuGetSearcherManager : SearcherManager<NuGetIndexSearcher>
    {
        public static readonly TimeSpan AuxiliaryDataRefreshRate = TimeSpan.FromHours(1);

        private readonly ILoader _loader;
        private readonly FrameworkLogger _logger;
        private readonly AzureDirectorySynchronizer _azureDirectorySynchronizer;

        private DateTime _auxiliaryDataReloaded = DateTime.MinValue;
        private readonly IDictionary<string, HashSet<string>> _owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly IDictionary<string, HashSet<string>> _curatedFeeds = new Dictionary<string, HashSet<string>>();
        private readonly Downloads _downloads = new Downloads();
        private readonly IReadOnlyDictionary<string, int[]> _docIdMapping;
        private IReadOnlyDictionary<string, int> _rankings;

        public NuGetSearcherManager(string indexName,
            FrameworkLogger logger,
            Lucene.Net.Store.Directory directory,
            ILoader loader,
            AzureDirectorySynchronizer azureDirectorySynchronizer)
            : base(directory)
        {
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            _logger = logger;

            IndexName = indexName;
            RegistrationBaseAddress = new Dictionary<string, Uri>();

            _loader = loader;
            _azureDirectorySynchronizer = azureDirectorySynchronizer;
        }

        public string IndexName { get; private set; }
        public IDictionary<string, Uri> RegistrationBaseAddress { get; }

        /// <summary>Initializes a <see cref="NuGetSearcherManager"/> instance.</summary>
        /// <param name="configuration">
        /// The configuration to read which primarily determines whether the resulting instance will read from the local
        /// disk or from blob storage.
        /// </param>
        /// <param name="directory">
        /// Optionally, the Lucene directory to read the index from. If <c>null</c> is provided, the directory
        /// implementation is determined based off of the configuration (<see cref="configuration"/>).
        /// </param>
        /// <param name="loader">
        /// Optionally, the loader used to read the JSON data files. If <c>null</c> is provided, the loader
        /// implementation is determined based off of the configuration (<see cref="configuration"/>).
        /// </param>
        /// <param name="loggerFactory">
        /// Optionally, the logger factory defined by the consuming application.
        /// </param>
        /// <returns>The resulting <see cref="NuGetSearcherManager"/> instance.</returns>
        public static NuGetSearcherManager Create(IConfiguration configuration,
            ILoggerFactory loggerFactory,
            Lucene.Net.Store.Directory directory = null,
            ILoader loader = null)
        {
            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            LoggerFactory = loggerFactory;

            NuGetSearcherManager searcherManager;
            var luceneDirectory = configuration.Get("Local.Lucene.Directory");

            if (!string.IsNullOrEmpty(luceneDirectory))
            {
                string dataDirectory = configuration.Get("Local.Data.Directory");

                searcherManager = new NuGetSearcherManager(
                    luceneDirectory,
                    loggerFactory.CreateLogger<NuGetSearcherManager>(),
                    directory ?? new SimpleFSDirectory(new DirectoryInfo(luceneDirectory)),
                    loader ?? new FileLoader(dataDirectory),
                    azureDirectorySynchronizer: null);
            }
            else
            {
                string storagePrimary = configuration.Get("Storage.Primary");
                string indexContainer = configuration.Get("Search.IndexContainer");
                string dataContainer = configuration.Get("Search.DataContainer");

                CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storagePrimary);

                if (string.IsNullOrEmpty(indexContainer))
                {
                    indexContainer = "ng-search-index";
                }

                if (string.IsNullOrEmpty(dataContainer))
                {
                    dataContainer = "ng-search-data";
                }

                AzureDirectorySynchronizer azureDirectorySynchronizer = null;
                if (directory == null)
                {
                    var sourceDirectory = new AzureDirectory(storageAccount, indexContainer);
                    directory = new RAMDirectory(sourceDirectory); // initial copy from storage to RAM

                    azureDirectorySynchronizer = new AzureDirectorySynchronizer(sourceDirectory, directory);
                }

                searcherManager = new NuGetSearcherManager(
                    indexContainer,
                    loggerFactory.CreateLogger<NuGetSearcherManager>(),
                    directory,
                    loader ?? new StorageLoader(storageAccount, dataContainer, loggerFactory.CreateLogger<StorageLoader>()),
                    azureDirectorySynchronizer: azureDirectorySynchronizer);
            }

            string registrationBaseAddress = configuration.Get("Search.RegistrationBaseAddress");
            searcherManager.RegistrationBaseAddress["http"] = MakeRegistrationBaseAddress("http", registrationBaseAddress);
            searcherManager.RegistrationBaseAddress["https"] = MakeRegistrationBaseAddress("https", registrationBaseAddress);

            return searcherManager;
        }

        internal static ILoggerFactory LoggerFactory { get; private set; }

        protected override IndexReader Reopen(IndexSearcher searcher)
        {
            if (_azureDirectorySynchronizer != null)
            {
                try
                {
                    _azureDirectorySynchronizer.Sync();
                }
                catch (Exception ex)
                {
                    _logger.LogError("NuGetSearcherManager.Reopen: failed to Sync from origin index.", ex);
                }
            }

            _logger.LogInformation("NuGetSearcherManager.Reopen: refreshing original IndexReader.");

            var stopwatch = Stopwatch.StartNew();
            var indexReader = searcher.IndexReader.Reopen();
            stopwatch.Stop();

            _logger.LogInformation("NuGetSearcherManager.Reopen: refreshed original IndexReader in {IndexReaderReopenDuration} seconds.", stopwatch.Elapsed.TotalSeconds);

            return indexReader;
        }

        /// <summary>
        /// This function is called whenever the SearcherManager decides it must re-create the IndexSearcher
        /// the key point to understand is that the auxillary data structures (in-memory indexes, filters and other lookups)
        /// absolutely must be kept in sync with the underlying IndexReader. This is because the shared key across
        /// all in-memory data is the Lucene docID and this can change following an index refresh.
        /// </summary>
        protected override NuGetIndexSearcher CreateSearcher(IndexReader reader)
        {
            _logger.LogInformation("NuGetSearcherManager.CreateSearcher");

            try
            {
                // (Re)load all the auxilliary data (if needed)
                try
                {
                    ReloadAuxiliaryDataIfExpired();
                }
                catch (Exception e)
                {
                    _logger.LogError("NuGetSearcherManager.CreateSearcher: Error loading auxiliary data.", e);
                    throw;
                }

                // The point of the IndexReaderProcessor is to allow us to loop of the IndexReader fewer times.
                // Looping over the reader, accessing the Document and then accessing the fields inside the Document are not
                // inexpensive operations especially when you are going to do that for every Document in the index.
                var indexReaderProcessor = new IndexReaderProcessor(enumerateSubReaders: true);

                var mappingHandler = new SegmentToMainReaderMappingHandler();
                indexReaderProcessor.AddHandler(mappingHandler);

                var downloadsMappingHandler = new DownloadDocIdMappingHandler(_downloads);
                indexReaderProcessor.AddHandler(downloadsMappingHandler);

                // We want to know about all package versions in the index (as they will be merged in V3 search result docs)
                var versionsHandler = new VersionsHandler(_downloads);
                indexReaderProcessor.AddHandler(versionsHandler);

                // Package rankings will be precalculated
                var rankingsHandler = new RankingsHandler(_rankings);
                indexReaderProcessor.AddHandler(rankingsHandler);

                // We want to be able to filter based by owner, so let's build a mapping of
                // owners and the Lucene document id's (per segment) for which they are the owner.
                //
                // Note that owners are not in the index as they can change along the way.
                var ownersHandler = new OwnersHandler(_owners);
                indexReaderProcessor.AddHandler(ownersHandler);

                // We want to be able to filter on unlisted/prerelease, so let's prepare building those filters.
                // Filters must be in terms of the structure of the underlying IndexReader. Specifically if the underlying
                // reader is Segmented then the filter must be too. Theoretically Lucene should be able to store a cached version of the
                // filter corresponding to each segment. We are not currently making use of that.

                // There are four flavors of Latest/Listed filter to reflect all the possible combinations.

                var h00 = new LatestListedHandler(includeUnlisted: false, includePrerelease: false);
                var h01 = new LatestListedHandler(includeUnlisted: false, includePrerelease: true);
                var h10 = new LatestListedHandler(includeUnlisted: true, includePrerelease: false);
                var h11 = new LatestListedHandler(includeUnlisted: true, includePrerelease: true);

                indexReaderProcessor.AddHandler(h00);
                indexReaderProcessor.AddHandler(h01);
                indexReaderProcessor.AddHandler(h10);
                indexReaderProcessor.AddHandler(h11);

                // We want to be able to filter on curated feeds as well
                var curatedFeedHandler = new CuratedFeedHandler(_curatedFeeds);
                indexReaderProcessor.AddHandler(curatedFeedHandler);

                // Traverse the index and segments
                try
                {
                    indexReaderProcessor.Process(reader);
                }
                catch (Exception e)
                {
                    _logger.LogError("NuGetSearcherManager.CreateSearcher: Error processing index reader.", e);
                    throw;
                }

                // Set filters
                var latest = new Filter[][]
                {
                    new Filter[] { h00.Result, h01.Result },
                    new Filter[] { h10.Result, h11.Result }
                };

                var latestBitSet = BitSetCollector.CreateBitSet(reader, latest[0][1]);
                var latestStableBitSet = BitSetCollector.CreateBitSet(reader, latest[0][0]);

                // Done loading index
                _logger.LogInformation("NuGetSearcherManager.CreateSearcher: Original {MaxDoc} (deletes: {NumDeletedDocs})", reader.MaxDoc, reader.NumDeletedDocs);

                // The point of having a specific subclass of the IndexSearcher is that we want to associate a bunch of auxilliary data along
                // with that specific instance of the reader. The lifetimes are assocaited, hense the inheritance relationship.

                _logger.LogInformation("NuGetSearcherManager.CreateSearcher: Creating a new NuGetIndexSearcher...");

                // Create a NuGetIndexSearcher
                return new NuGetIndexSearcher(
                    this,
                    reader,
                    reader.CommitUserData,
                    curatedFeedHandler.Result,
                    latest,
                    mappingHandler.Result,
                    _downloads,
                    versionsHandler.Result,
                    rankingsHandler.Result,
                    latestBitSet,
                    latestStableBitSet,
                    ownersHandler.Result);
            }
            catch (Exception e)
            {
                _logger.LogError("NuGetSearcherManager.CreateSearcher: An error occurred.", e);
                return null;
            }
        }

        private void ReloadAuxiliaryDataIfExpired()
        {
            if (_auxiliaryDataReloaded < DateTime.UtcNow - AuxiliaryDataRefreshRate)
            {
                IndexingUtils.Load("owners.json", _loader, _logger, _owners);
                IndexingUtils.Load("curatedfeeds.json", _loader, _logger, _curatedFeeds);
                _downloads.Load("downloads.v1.json", _loader, _logger);
                _rankings = DownloadRankings.Load("rankings.v1.json", _loader, _logger);

                _auxiliaryDataReloaded = DateTime.UtcNow;
            }
        }

        protected override void Warm(NuGetIndexSearcher searcher)
        {
            _logger.LogInformation("NuGetSearcherManager.Warm");
            var stopwatch = Stopwatch.StartNew();

            // Warmup search (query all documents)
            searcher.Search(new MatchAllDocsQuery(), 1);

            // Warmup search (query for a specific term with rankings)
            var query = NuGetQuery.MakeQuery("newtonsoft.json", searcher.Owners);
            var boostedQuery = new DownloadsBoostedQuery(query,
                searcher.DocIdMapping,
                searcher.Downloads,
                searcher.Rankings);

            searcher.Search(boostedQuery, 5);

            // Warmup search (with a sort so Lucene field caches are populated)
            var sort1 = new Sort(new SortField("LastEditedDate", SortField.INT, reverse: true));
            var sort2 = new Sort(new SortField("PublishedDate", SortField.INT, reverse: true));
            var sort3 = new Sort(new SortField("SortableTitle", SortField.STRING, reverse: false));
            var sort4 = new Sort(new SortField("SortableTitle", SortField.STRING, reverse: true));

            var topDocs1 = searcher.Search(boostedQuery, null, 250, sort1);
            var topDocs2 = searcher.Search(boostedQuery, null, 250, sort2);
            var topDocs3 = searcher.Search(boostedQuery, null, 250, sort3);
            var topDocs4 = searcher.Search(boostedQuery, null, 250, sort4);

            // Warmup field caches by fetching data from them
            using (var writer = new JsonTextWriter(new StreamWriter(new MemoryStream())))
            {
                ResponseFormatter.WriteV2Result(writer, searcher, topDocs1, 0, 250);
                ResponseFormatter.WriteSearchResult(writer, searcher, "http", topDocs1, 0, 250, false, false, boostedQuery);

                ResponseFormatter.WriteV2Result(writer, searcher, topDocs2, 0, 250);
                ResponseFormatter.WriteSearchResult(writer, searcher, "http", topDocs2, 0, 250, false, false, boostedQuery);

                ResponseFormatter.WriteV2Result(writer, searcher, topDocs3, 0, 250);
                ResponseFormatter.WriteSearchResult(writer, searcher, "http", topDocs3, 0, 250, false, false, boostedQuery);

                ResponseFormatter.WriteV2Result(writer, searcher, topDocs4, 0, 250);
                ResponseFormatter.WriteSearchResult(writer, searcher, "http", topDocs4, 0, 250, false, false, boostedQuery);
            }

            // Done, we're warm.
            stopwatch.Stop();
            _logger.LogInformation("NuGetSearcherManager.Warm: completed in {IndexSearcherWarmDuration} seconds.",
                stopwatch.Elapsed.TotalSeconds);
        }

        private static Uri MakeRegistrationBaseAddress(string scheme, string registrationBaseAddress)
        {
            Uri original = new Uri(registrationBaseAddress);
            if (original.Scheme == scheme)
            {
                return original;
            }
            else
            {
                var builder = new UriBuilder(original)
                {
                    Scheme = scheme,
                    Port = -1
                };

                return builder.Uri;
            }
        }
    }
}