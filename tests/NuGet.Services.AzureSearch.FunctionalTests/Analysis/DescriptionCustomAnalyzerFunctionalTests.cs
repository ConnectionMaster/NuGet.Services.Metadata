﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Threading.Tasks;
using NuGet.Services.AzureSearch.FunctionalTests.Support;
using Xunit;

namespace NuGet.Services.AzureSearch.FunctionalTests
{
    public class DescriptionCustomAnalyzerFunctionalTests : AzureIndexFunctionalTests
    {
        private const string AnalyzerName = "nuget_description_analyzer";

        public DescriptionCustomAnalyzerFunctionalTests(CommonFixture fixture)
            : base(fixture)
        {
        }

        [AnalysisTheory]
        [MemberData(nameof(TokenizationData.TruncatesTokens), MemberType = typeof(TokenizationData))]
        [MemberData(nameof(TokenizationData.LowercasesTokens), MemberType = typeof(TokenizationData))]
        [MemberData(nameof(TokenizationData.SplitsTokensOnSpecialCharactersAndLowercases), MemberType = typeof(TokenizationData))]
        [MemberData(nameof(TokenizationData.LowercasesAndAddsTokensOnCasingAndNonAlphaNumeric), MemberType = typeof(TokenizationData))]
        [MemberData(nameof(TokenizationData.AddsTokensOnNonAlphaNumericAndRemovesStopWords), MemberType = typeof(TokenizationData))]
        public async Task ProducesExpectedTokens(string input, string[] expectedTokens)
        {
            var actualTokens = new HashSet<string>(await AnalyzeAsync(AnalyzerName, input));

            foreach (var expectedToken in expectedTokens)
            {
                Assert.Contains(expectedToken, actualTokens);
            }

            Assert.Equal(expectedTokens.Length, actualTokens.Count);
        }
    }
}
