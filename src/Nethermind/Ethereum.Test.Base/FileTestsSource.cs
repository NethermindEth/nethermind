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

                byte[] json = File.ReadAllBytes(_fileName);

                return testType switch
                {
                    TestType.State => JsonToEthereumTest.ConvertStateTest(json),
                    TestType.Transaction => JsonToEthereumTest.ConvertTransactionTests(json),
                    _ => JsonToEthereumTest.ConvertToBlockchainTests(json)
                };
            }
            catch (Exception e)
            {
                return [new FailedToLoadTest { Name = _fileName, LoadFailure = $"Failed to load: {e}" }];
            }
        }
    }
}
