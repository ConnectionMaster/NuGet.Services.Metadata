﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using FrameworkLogger = Microsoft.Extensions.Logging.ILogger;

namespace NuGet.Indexing
{
    public static class Downloads
    {
        public static IDictionary<string, IDictionary<string, int>> Load(string name, ILoader loader, FrameworkLogger logger)
        {
            var result = new Dictionary<string, IDictionary<string, int>>();

            // The data in downloads.v1.json will be an array of Package records - which has Id, Array of Versions and download count.
            // Sample.json : [["AutofacContrib.NSubstitute",["2.4.3.700",406],["2.5.0",137]],["Assman.Core",["2.0.7",138]]....
            using (var jsonReader = loader.GetReader(name))
            {
                try
                {
                    jsonReader.Read();

                    while (jsonReader.Read())
                    {
                        try
                        {
                            if (jsonReader.TokenType == JsonToken.StartArray)
                            {
                                var record = JToken.ReadFrom(jsonReader);
                                var id = record[0].ToString().ToLowerInvariant();
                                // The second entry in each record should be an array of versions, if not move on to next entry.
                                // This is a check to safe guard against invalid entries.
                                if (record.Count() == 2 && record[1].Type != JTokenType.Array)
                                {
                                    continue;
                                }

                                var versions = new Dictionary<string, int>();
                                foreach (var token in record)
                                {
                                    if (token != null && token.Count() == 2)
                                    {
                                        var version = token[0].ToString().ToLowerInvariant();
                                        // Check for duplicate versions before adding.
                                        if (!versions.ContainsKey(version))
                                        {
                                            versions.Add(version, token[1].ToObject<int>());
                                        }
                                    }
                                }

                                //Check for duplicate Ids before adding to dict.
                                if (!result.ContainsKey(id))
                                {
                                    result.Add(id, versions);
                                }

                            }
                        }
                        catch (JsonReaderException ex)
                        {
                            logger.LogInformation("Invalid entry found in downloads.v1.json. Exception Message : {0}", ex.Message);
                        }
                    }
                }
                catch (JsonReaderException ex)
                {
                    logger.LogError("Data present in downloads.v1.json is invalid. Couldn't get download data.", ex);
                }
            }

            return result;
        }
    }
}