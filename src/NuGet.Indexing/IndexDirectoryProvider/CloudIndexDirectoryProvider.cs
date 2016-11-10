﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Diagnostics;
using System.Threading.Tasks;
using Lucene.Net.Store;
using Lucene.Net.Store.Azure;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using NuGet.Services.Configuration;
using FrameworkLogger = Microsoft.Extensions.Logging.ILogger;

namespace NuGet.Indexing.IndexDirectoryProvider
{
    /// <summary>
    /// Maintains an index on the cloud. Provides a synchronizer and a reload method to refresh the index.
    /// </summary>
    public class CloudIndexDirectoryProvider : IIndexDirectoryProvider
    {
        private readonly FrameworkLogger _logger;

        private readonly ISettingsProvider _settings;

        private Directory _directory;
        private string _indexContainerName;
        private string _storageAccountConnectionString;
        private AzureDirectorySynchronizer _synchronizer;

        public static async Task<IIndexDirectoryProvider> Create(ISettingsProvider settings, FrameworkLogger logger)
        {
            var indexSynchronizer = new CloudIndexDirectoryProvider(settings, logger);
            await indexSynchronizer.Reload();
            return indexSynchronizer;
        }

        protected CloudIndexDirectoryProvider(ISettingsProvider settings, FrameworkLogger logger)
        {
            _logger = logger;
            _settings = settings;
        }

        public Directory GetDirectory()
        {
            return _directory;
        }

        public string GetIndexContainerName()
        {
            return _indexContainerName;
        }

        public AzureDirectorySynchronizer GetSynchronizer()
        {
            return _synchronizer;
        }

        public async Task<bool> Reload()
        {
            // If we have a directory and the index container has not changed, we don't need to reload.
            // We don't want to reload the index unless necessary.
            var newStorageAccountConnectionString = await _settings.GetOrThrow<string>(IndexingSettings.StoragePrimary);
            var newIndexContainerName = await _settings.GetOrDefault(IndexingSettings.IndexContainer, IndexingSettings.IndexContainerDefault);
            if (_directory != null && 
                newStorageAccountConnectionString == _storageAccountConnectionString &&
                newIndexContainerName == _indexContainerName)
            {
                return false;
            }
            
            _storageAccountConnectionString = newStorageAccountConnectionString;
            _indexContainerName = newIndexContainerName;

            _logger.LogInformation(
                "Recognized index configuration change. Reloading index with new settings. Storage Account Name = {StorageAccountName}, Container = {IndexContainerName}",
                _storageAccountConnectionString, _indexContainerName);

            var stopwatch = Stopwatch.StartNew();

            var storageAccount = CloudStorageAccount.Parse(_storageAccountConnectionString);

            var sourceDirectory = new AzureDirectory(storageAccount, _indexContainerName);
            _directory = new RAMDirectory(sourceDirectory); // Copy the directory from Azure storage to RAM.

            _synchronizer = new AzureDirectorySynchronizer(sourceDirectory, _directory);

            stopwatch.Stop();
            _logger.LogInformation($"Index reload completed and took {stopwatch.Elapsed.Seconds} seconds.");

            return true;
        }
    }
}
