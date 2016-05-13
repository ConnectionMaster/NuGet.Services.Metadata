﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Lucene.Net.Store;
using Microsoft.Extensions.Logging;
using Microsoft.Owin;
using Microsoft.Owin.FileSystems;
using Microsoft.Owin.StaticFiles;
using Microsoft.Owin.StaticFiles.Infrastructure;
using NuGet.ApplicationInsights.Owin;
using NuGet.Indexing;
using NuGet.Services.Logging;
using Owin;
using Serilog.Events;
using SerilogWeb.Classic;
using SerilogWeb.Classic.Enrichers;

[assembly: OwinStartup("NuGet.Services.BasicSearch", typeof(NuGet.Services.BasicSearch.Startup))]

namespace NuGet.Services.BasicSearch
{
    public class Startup
    {
        private ILogger _logger;
        private Timer _indexReloadTimer;
        private NuGetSearcherManager _searcherManager;
        private int _gate;
        private ResponseWriter _responseWriter;
        private SearchTelemetryClient _searchTelemetryClient;

        public void Configuration(IAppBuilder app, IConfiguration configuration, Directory directory, ILoader loader)
        {
            // Configure 
            Logging.ApplicationInsights.Initialize(configuration.Get("serilog:ApplicationInsightsInstrumentationKey"));

            // Create telemetry sink
            _searchTelemetryClient = new SearchTelemetryClient();

            // Create an ILoggerFactory
            var loggerConfiguration = LoggingSetup.CreateDefaultLoggerConfiguration(withConsoleLogger: false)
                .Enrich.With<HttpRequestIdEnricher>()
                .Enrich.With<HttpRequestTraceIdEnricher>()
                .Enrich.With<HttpRequestTypeEnricher>()
                .Enrich.With<HttpRequestUrlReferrerEnricher>()
                .Enrich.With<HttpRequestUserAgentEnricher>()
                .Enrich.With<HttpRequestRawUrlEnricher>();

            // Customize Serilog web logging - https://github.com/serilog-web/classic
            ApplicationLifecycleModule.RequestLoggingLevel = LogEventLevel.Warning;
            ApplicationLifecycleModule.LogPostedFormData = LogPostedFormDataOption.OnlyOnError;

            var loggerFactory = LoggingSetup.CreateLoggerFactory(loggerConfiguration);

            // Create a logger that is scoped to this class (only)
            _logger = loggerFactory.CreateLogger<Startup>();

            _logger.LogInformation(LogMessages.AppStartup);

            // Correlate requests
            app.Use(typeof(CorrelationIdMiddleware));

            // Add Application Insights
            app.Use(typeof(ApplicationInsightsMiddleware));
            
            // Search test console
            app.Use(typeof(SearchConsoleMiddleware));
            app.UseStaticFiles(new StaticFileOptions(new SharedOptions
            {
                RequestPath = new PathString("/console"),
                FileSystem = new EmbeddedResourceFileSystem(typeof(Startup).Assembly, "NuGet.Services.BasicSearch.Console")
            }));

            // Start the service running - the Lucene index needs to be reopened regularly on a background thread
            var searchIndexRefresh = configuration.Get("Search.IndexRefresh") ?? "300";
            int seconds;
            if (!int.TryParse(searchIndexRefresh, out seconds))
            {
                seconds = 120;
            }

            _logger.LogInformation(LogMessages.SearchIndexRefreshConfiguration, seconds);

            if (InitializeSearcherManager(configuration, directory, loader, loggerFactory))
            {
                var intervalInMs = seconds * 1000;

                _gate = 0;
                _indexReloadTimer = new Timer(ReopenCallback, 0, intervalInMs, intervalInMs);
            }

            _responseWriter = new ResponseWriter();

            app.Run(InvokeAsync);
        }

        public void Configuration(IAppBuilder app)
        {
            ServicePointManager.DefaultConnectionLimit = Int32.MaxValue;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;

            Configuration(app, new ConfigurationService(), null, null);
        }

        private void ReopenCallback(object state)
        {
            try
            {
                int val = Interlocked.Increment(ref _gate);
                if (val > 1)
                {
                    _logger.LogInformation(LogMessages.SearchIndexAlreadyReopened, Thread.CurrentThread.ManagedThreadId);
                    Interlocked.Decrement(ref _gate);
                    return;
                }

                _logger.LogInformation(LogMessages.SearchIndexReopenStarted, Thread.CurrentThread.ManagedThreadId);

                try
                {
                    var stopwatch = Stopwatch.StartNew();

                    _searcherManager.MaybeReopen();

                    stopwatch.Stop();

                    _logger.LogInformation(LogMessages.SearchIndexReopenCompleted, stopwatch.Elapsed.TotalSeconds,
                        Thread.CurrentThread.ManagedThreadId);

                    _searchTelemetryClient.TrackMetric(
                        SearchTelemetryClient.MetricName.SearchIndexReopenDuration, stopwatch.Elapsed.TotalSeconds);

                    TrackIndexMetrics(_searcherManager, _searchTelemetryClient);
                }
                finally
                {
                    Interlocked.Decrement(ref _gate);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(LogMessages.SearchIndexReopenFailed, e);

                _searchTelemetryClient.TrackMetric(SearchTelemetryClient.MetricName.SearchIndexReopenFailed, 1);
            }
        }

        private bool InitializeSearcherManager(IConfiguration configuration, Directory directory, ILoader loader, ILoggerFactory loggerFactory)
        {
            const int maxRetries = 5;

            try
            {
                Retry.Incremental(
                    () =>
                    {
                        var stopwatch = Stopwatch.StartNew();

                        _searcherManager = NuGetSearcherManager.Create(configuration, loggerFactory, directory, loader);
                        _searcherManager.Open();

                        stopwatch.Stop();

                        _searchTelemetryClient.TrackMetric(
                            SearchTelemetryClient.MetricName.SearchIndexReopenDuration, stopwatch.Elapsed.TotalSeconds);

                        TrackIndexMetrics(_searcherManager, _searchTelemetryClient);
                    },
                    shouldRetry: e =>
                    {
                        // Retry on any exception (but log it)
                        _logger.LogError("Startup: An error occurred initializing searcher manager. Going to retry...", e);
                        _searchTelemetryClient.TrackMetric(SearchTelemetryClient.MetricName.SearchIndexReopenFailed, 1);

                        return true;
                    },
                    maxRetries: maxRetries,
                    waitIncrement: TimeSpan.FromSeconds(1));
                
                return true;
            }
            catch (Exception e)
            {
                _logger.LogCritical("Startup: A critical error occurred initializing searcher manager. Number of retries exhausted.", e);
                _searchTelemetryClient.TrackMetric(SearchTelemetryClient.MetricName.SearchIndexReopenFailed, maxRetries);

                return false;
            }
        }

        private void TrackIndexMetrics(NuGetSearcherManager searcherManager, SearchTelemetryClient searchTelemetryClient)
        {
            var searcher = searcherManager.Get();
            try
            {
                // Track number of documents in index
                searchTelemetryClient.TrackMetric(SearchTelemetryClient.MetricName.LuceneNumDocs, searcher.IndexReader.NumDocs());

                // Track time between Lucene commit and reopen
                string temp;
                if (searcher.CommitUserData.TryGetValue("commitTimeStamp", out temp))
                {
                    var commitTimestamp = DateTimeOffset.Parse(temp, null, DateTimeStyles.AssumeUniversal);
                    
                    searchTelemetryClient.TrackMetric(SearchTelemetryClient.MetricName.LuceneLoadLag,
                        (searcher.LastReopen - commitTimestamp.UtcDateTime).TotalSeconds, 
                        new Dictionary<string, string>()
                        {
                            { SearchTelemetryClient.MetricName.LuceneLastReopen, searcher.LastReopen.ToString("o") },
                            { SearchTelemetryClient.MetricName.LuceneCommitTimestamp, commitTimestamp.UtcDateTime.ToString("o") }
                        });
                }
            }
            finally
            {
                searcherManager.Release(searcher);
            }
        }

        public async Task InvokeAsync(IOwinContext context)
        {
            try
            {
                if (_searcherManager == null)
                {
                    _logger.LogInformation(LogMessages.SearcherManagerNotInitialized);
                    _searchTelemetryClient.TrackMetric(SearchTelemetryClient.MetricName.SearcherManagerNotInitialized, 1);

                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    await context.Response.WriteAsync("UNINITIALIZED");
                }
                else
                {
                    switch (context.Request.Path.Value)
                    {
                        case "/":
                            context.Response.StatusCode = (int)HttpStatusCode.OK;
                            await context.Response.WriteAsync("READY");
                            break;
                        case "/find":
                            await ServiceEndpoints.FindAsync(context, _searcherManager, _responseWriter);
                            break;
                        case "/query":
                            await ServiceEndpoints.V3SearchAsync(context, _searcherManager, _responseWriter);
                            break;
                        case "/autocomplete":
                            await ServiceEndpoints.AutoCompleteAsync(context, _searcherManager, _responseWriter);
                            break;
                        case "/search/query":
                            await ServiceEndpoints.V2SearchAsync(context, _searcherManager, _responseWriter);
                            break;
                        case "/rankings":
                            await ServiceEndpoints.RankingsAsync(context, _searcherManager, _responseWriter);
                            break;
                        case "/search/diag":
                            await ServiceEndpoints.Stats(context, _searcherManager, _responseWriter);
                            break;
                        default:
                            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            await context.Response.WriteAsync("UNRECOGNIZED");
                            break;
                    }
                }
            }
            catch (ClientException e)
            {
                await _responseWriter.WriteResponseAsync(context, e);
            }
            catch (Exception e)
            {
                await _responseWriter.WriteResponseAsync(context, e, _logger);
            }
        }
    }
}
