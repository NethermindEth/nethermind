// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;

namespace Ethereum.Test.Base
{
    public class FileTestsSource(string fileName, string? wildcard = null)
    {
        private readonly string _fileName = fileName ?? throw new ArgumentNullException(nameof(fileName));

        public IEnumerable<EthereumTest> LoadTests(TestType testType)
        {
            try
            {
                if (Path.GetFileName(_fileName).StartsWith('.'))
                {
                    return [];
                }

                if (wildcard is not null && !_fileName.Contains(wildcard))
                {
                    return [];
                }

                // Read as UTF-8 bytes directly — avoids the intermediate string allocation
                // from File.ReadAllText. System.Text.Json can deserialize from byte spans
                // without encoding conversion overhead.
                byte[] jsonBytes = File.ReadAllBytes(_fileName);

                return testType switch
                {
                    TestType.State => JsonToEthereumTest.ConvertStateTest(jsonBytes),
                    _ => JsonToEthereumTest.ConvertToBlockchainTests(jsonBytes)
                };
            }
            catch (Exception e)
            {
                return [new FailedToLoadTest { Name = _fileName, LoadFailure = $"Failed to load: {e}" }];
            }
        }
    }
}
