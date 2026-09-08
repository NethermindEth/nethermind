// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Logging;
using NUnit.Framework;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Serialization.Json;

namespace Nethermind.Runner.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class ChainSpecFilesTests
    {
        private readonly ChainSpecFileLoader _loader;

        public ChainSpecFilesTests() => _loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance);

        [TestCase("foundation", 1UL)]
        [TestCase("chainspec/foundation", 1UL)]
        [TestCase("chainspec/foundation.json", 1UL)]
        public void different_formats_to_chainSpecPath(string chainSpecPath, ulong chainId) =>
            Assert.That(_loader.LoadEmbeddedOrFromFile(chainSpecPath).ChainId, Is.EqualTo(chainId));

        [TestCase("testspec.json", 0x55UL)]
        public void ChainSpec_from_file(string chainSpecPath, ulong chainId) =>
            Assert.That(_loader.LoadEmbeddedOrFromFile(chainSpecPath).ChainId, Is.EqualTo(chainId));

        [TestCase("chainspec/custom_chainspec_that_does_not_exist.json")]
        public void ChainSpecNotFound(string chainSpecPath)
        {
            Func<ChainSpec> tryLoad = () => _loader.LoadEmbeddedOrFromFile(chainSpecPath);
            Assert.That(tryLoad, Throws.TypeOf<FileNotFoundException>());
        }

        [TestCase("chainspec/op-mainnet.json.zst", 10UL)]
        public void Zstandard_Compressed_ChainSpec(string chainSpecPath, ulong chainId) =>
            Assert.That(_loader.LoadEmbeddedOrFromFile(chainSpecPath).ChainId, Is.EqualTo(chainId));
    }
}
