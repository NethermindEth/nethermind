// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Test.Base
{
    public class FileTestsSource
    {
        private readonly string _fileName;
        private readonly string? _wildcard;

        public FileTestsSource(string fileName, string? wildcard = null)
        {
            _fileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
            _wildcard = wildcard;
        }

        public IEnumerable<EthereumTest> LoadTests(TestType testType)
        {
            try
            {
                if (Path.GetFileName(_fileName).StartsWith('.'))
                {
                    return [];
                }

                if (_wildcard is not null && !_fileName.Contains(_wildcard))
                {
                    return [];
                }

                string json = File.ReadAllText(_fileName, Encoding.Default);

                return testType switch
                {
                    TestType.Eof => JsonToEthereumTest.ConvertToEofTests(json),
                    TestType.State => JsonToEthereumTest.ConvertStateTest(json),
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
