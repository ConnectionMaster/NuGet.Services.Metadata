﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Versioning;

namespace NuGet.Indexing
{
    public static class CatalogPackageMetadataExtraction
    {
        public static IDictionary<string, string> MakePackageMetadata(JObject catalogItem)
        {
            var extractor = new Extractor();
            return extractor.Extract(catalogItem);
        }

        private class Extractor
        {
            private JObject _catalog;
            private CatalogPackageReader _reader;
            private Dictionary<string, string> _metadata;

            public IDictionary<string, string> Extract(JObject catalog)
            {
                _catalog = catalog;
                _reader = new CatalogPackageReader(_catalog);
                _metadata = new Dictionary<string, string>();

                AddString("id");
                AddString("version");
                AddString("verbatimVersion", "originalVersion");
                AddString("title");
                AddString("description");
                AddString("summary");
                AddString("authors");
                AddStringArray("tags");

                AddListed();
                AddString("created");
                AddString("published");
                AddString("lastEdited");

                AddString("iconUrl");
                AddString("projectUrl");
                AddString("minClientVersion");
                AddString("releaseNotes");
                AddString("copyright");
                AddString("language");
                AddString("licenseUrl");
                AddString("packageHash");
                AddString("packageHashAlgorithm");
                AddString("packageSize");
                AddString("requireLicenseAcceptance");

                AddFlattenedDependencies();
                AddFlattenedPackageTypes();
                AddSupportedFrameworks();

                return _metadata;
            }

            private void AddString(string source, string destination = null)
            {
                var value = _catalog[source];
                if (value == null)
                {
                    return;
                }

                _metadata[destination ?? source] = JTokenToString(value);
            }

            private string JTokenToString(JToken value)
            {
                if (value == null)
                {
                    return null;
                }

                if (value.Type == JTokenType.Date)
                {
                    return value.Value<DateTimeOffset>().ToString("o");
                }
                else
                {
                    return (string)value;
                }
            }

            private void AddStringArray(string source, string destination = null)
            {
                var value = _catalog[source];
                if (value == null)
                {
                    return;
                }

                string joined = string.Join(" ", value.Select(JTokenToString));
                _metadata[destination ?? source] = joined;
            }

            private void AddListed()
            {
                var listed = (string)_catalog["listed"];
                var published = _catalog["published"];
                if (listed == null)
                {
                    if (published != null && ((DateTime)published).ToString("yyyyMMdd") == "19000101")
                    {
                        listed = "false";
                    }
                    else
                    {
                        listed = "true";
                    }
                }

                _metadata["listed"] = listed;
            }

            private void AddFlattenedPackageTypes()
            {
                var packageTypes = _reader.GetPackageTypes();
                
                if (packageTypes.Count > 0)
                {
                    _metadata["flattenedPackageTypes"] = string.Join("|", 
                        packageTypes.Select(packageType => packageType.Name + ":" + packageType.Version.ToString()));
                }
            }

            private void AddFlattenedDependencies()
            {
                var dependencyGroups = _reader.GetPackageDependencies().ToList();

                var builder = new StringBuilder();
                foreach (var dependencyGroup in dependencyGroups)
                {
                    if (dependencyGroup.Packages.Any())
                    {
                        // Add packages list
                        foreach (var packageDependency in dependencyGroup.Packages)
                        {
                            AddFlattennedPackageDependency(dependencyGroup, packageDependency, builder);
                        }
                    }
                    else
                    {
                        // Add empty framework dependency
                        if (builder.Length > 0)
                        {
                            builder.Append("|");
                        }

                        builder.Append(":");
                        AddFlattenedFrameworkDependency(dependencyGroup, builder);
                    }
                }

                if (builder.Length > 0)
                {
                    _metadata["flattenedDependencies"] = builder.ToString();
                }
            }

            private void AddFlattennedPackageDependency(
                PackageDependencyGroup dependencyGroup,
                Packaging.Core.PackageDependency packageDependency,
                StringBuilder builder)
            {
                if (builder.Length > 0)
                {
                    builder.Append("|");
                }

                builder.Append(packageDependency.Id);
                builder.Append(":");
                if (!packageDependency.VersionRange.Equals(VersionRange.All))
                {
                    builder.Append(packageDependency.VersionRange?.ToString("S", new VersionRangeFormatter()));
                }

                AddFlattenedFrameworkDependency(dependencyGroup, builder);
            }

            private void AddFlattenedFrameworkDependency(PackageDependencyGroup dependencyGroup, StringBuilder builder)
            {
                if (!SpecialFrameworks.Contains(dependencyGroup.TargetFramework))
                {
                    try
                    {
                        builder.Append(":");
                        builder.Append(dependencyGroup.TargetFramework?.GetShortFolderName());
                    }
                    catch (FrameworkException)
                    {
                        // ignoring FrameworkException on purpose - we don't want the job crashing
                        // whenever someone uploads an unsupported framework
                    }
                }
            }

            private void AddSupportedFrameworks()
            {
                // Parse files for framework names
                List<NuGetFramework> supportedFrameworksFromReader = null;
                try
                {
                    supportedFrameworksFromReader = _reader
                        .GetSupportedFrameworks()
                        .ToList();
                }
                catch (ArgumentException ex) when (ex.Message.ToLowerInvariant().StartsWith("invalid portable"))
                {
                    // ignoring ArgumentException that denotes invalid portable framework on purpose
                    // - we don't want the job crashing whenever someone uploads an unsupported framework
                    Trace.TraceError("CatalogPackageMetadataExtraction.AddSupportedFrameworks exception: " + ex.Message);
                    return;
                }
                catch (FrameworkException ex)
                {
                    // ignoring FrameworkException on purpose - we don't want the job crashing
                    // whenever someone uploads an unsupported framework
                    Trace.TraceError("CatalogPackageMetadataExtraction.AddSupportedFrameworks exception: " + ex.Message);
                    return;
                }

                // Filter out special frameworks + get short framework names
                var supportedFrameworks = supportedFrameworksFromReader
                    .Except(SpecialFrameworks)
                    .Select(f =>
                    {
                        try
                        {
                            return f.GetShortFolderName();
                        }
                        catch (FrameworkException)
                        {
                            // ignoring FrameworkException on purpose - we don't want the job crashing
                            // whenever someone uploads an unsupported framework
                            return null;
                        }
                    })
                    .Where(f => !String.IsNullOrEmpty(f))
                    .ToArray();

                if (supportedFrameworks.Any())
                {
                    _metadata["supportedFrameworks"] = string.Join("|", supportedFrameworks);
                }
            }

            private IEnumerable<NuGetFramework> SpecialFrameworks => new[]
            {
                NuGetFramework.AnyFramework,
                NuGetFramework.AgnosticFramework,
                NuGetFramework.UnsupportedFramework
            };
        }
    }
}