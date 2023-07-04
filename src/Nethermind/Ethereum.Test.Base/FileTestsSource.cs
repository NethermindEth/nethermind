// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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

        public IEnumerable<GeneralStateTest> LoadGeneralStateTests()
        {
            try
            {
                if (Path.GetFileName(_fileName).StartsWith("."))
                {
                    return Enumerable.Empty<GeneralStateTest>();
                }

                if (_wildcard != null && !_fileName.Contains(_wildcard))
                {
                    return Enumerable.Empty<GeneralStateTest>();
                }

                string json = File.ReadAllText(_fileName);
                return JsonToEthereumTest.Convert(json);
            }
            catch (Exception e)
            {
                return Enumerable.Repeat(new GeneralStateTest { Name = _fileName, LoadFailure = $"Failed to load: {e}" }, 1);
            }
        }

        public IEnumerable<BlockchainTest> LoadBlockchainTests()
        {
            try
            {
                if (Path.GetFileName(_fileName).StartsWith("."))
                {
                    return Enumerable.Empty<BlockchainTest>();
                }

                if (_wildcard != null && !_fileName.Contains(_wildcard))
                {
                    return Enumerable.Empty<BlockchainTest>();
                }

                string json = File.ReadAllText(_fileName, Encoding.Default);

                return JsonToEthereumTest.ConvertToBlockchainTests(json);
            }
            catch (Exception e)
            {
                return Enumerable.Repeat(new BlockchainTest { Name = _fileName, LoadFailure = $"Failed to load: {e}" }, 1);
            }
        }
    }
}
