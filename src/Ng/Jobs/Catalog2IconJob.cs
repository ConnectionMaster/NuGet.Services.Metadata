﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NuGet.Protocol.Catalog;
using NuGet.Services.Configuration;
using NuGet.Services.Metadata.Catalog;
using NuGet.Services.Metadata.Catalog.Icons;
using NuGet.Services.Metadata.Catalog.Persistence;

namespace Ng.Jobs
{
    public class Catalog2IconJob : LoopingNgJob
    {
        private const int DegreeOfParallelism = 100;
        private IconsCollector _collector;
        private DurableCursor _front;

        public Catalog2IconJob(ITelemetryService telemetryService, ILoggerFactory loggerFactory)
            : base(telemetryService, loggerFactory)
        {
        }

        protected override void Init(IDictionary<string, string> arguments, CancellationToken cancellationToken)
        {
            ServicePointManager.DefaultConnectionLimit = DegreeOfParallelism;

            var verbose = arguments.GetOrDefault(Arguments.Verbose, false);
            var packageStorageBase = arguments.GetOrThrow<string>(Arguments.ContentBaseAddress);
            var auxStorageFactory = CreateAuxStorageFactory(arguments, verbose);
            var targetStorageFactory = CreateTargetStorageFactory(arguments, verbose);
            var packageStorage = new AzureStorage(new Uri(packageStorageBase), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(10), false, false, true, null);
            var source = arguments.GetOrThrow<string>(Arguments.Source);
            var auxStorage = auxStorageFactory.Create();
            var iconProcessor = new IconProcessor(TelemetryService, LoggerFactory.CreateLogger<IconProcessor>());
            var httpHandlerFactory = CommandHelpers.GetHttpMessageHandlerFactory(TelemetryService, verbose);
            var httpMessageHandler = httpHandlerFactory();
            var httpClient = new HttpClient(httpMessageHandler);
            var simpleHttpClient = new SimpleHttpClient(httpClient, LoggerFactory.CreateLogger<SimpleHttpClient>());
            var catalogClient = new CatalogClient(simpleHttpClient, LoggerFactory.CreateLogger<CatalogClient>());
            var httpResponseProvider = new HttpResponseMessageProvider(httpClient);

            var leafProcessor = new CatalogLeafDataProcessor(
                packageStorage,
                auxStorage,
                iconProcessor,
                httpResponseProvider,
                TelemetryService,
                LoggerFactory.CreateLogger<CatalogLeafDataProcessor>());

            _collector = new IconsCollector(
                new Uri(source),
                TelemetryService,
                targetStorageFactory,
                catalogClient,
                leafProcessor,
                () => httpMessageHandler,
                LoggerFactory.CreateLogger<IconsCollector>());
            _front = new DurableCursor(auxStorage.ResolveUri("c2icursor.json"), auxStorage, DateTime.MinValue.ToUniversalTime());
        }

        protected override async Task RunInternalAsync(CancellationToken cancellationToken)
        {
            bool run;
            do
            {
                run = await _collector.RunAsync(_front, MemoryCursor.CreateMax(), cancellationToken);
            } while (run);
        }

        private IStorageFactory CreateAuxStorageFactory(IDictionary<string, string> arguments, bool verbose)
        {
            return CommandHelpers.CreateSuffixedStorageFactory("Aux", arguments, verbose);
        }

        private IStorageFactory CreateTargetStorageFactory(IDictionary<string, string> arguments, bool verbose)
        {
            return CommandHelpers.CreateSuffixedStorageFactory("Target", arguments, verbose);
        }
    }
}
