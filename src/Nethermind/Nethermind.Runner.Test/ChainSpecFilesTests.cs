// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using FluentAssertions;
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

        public ChainSpecFilesTests()
        {
            _loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        }

        [TestCase("foundation", 1UL)]
        [TestCase("chainspec/foundation", 1UL)]
        [TestCase("chainspec/foundation.json", 1UL)]
        public void different_formats_to_chainSpecPath(string chainSpecPath, ulong chainId)
        {
            _loader.LoadEmbeddedOrFromFile(chainSpecPath).Should()
                .Match<ChainSpec>(cs => cs.ChainId == chainId);
        }

        [TestCase("testspec.json", 0x55UL)]
        public void ChainSpec_from_file(string chainSpecPath, ulong chainId)
        {
            _loader.LoadEmbeddedOrFromFile(chainSpecPath).Should()
                .Match<ChainSpec>(cs => cs.ChainId == chainId);
        }

        // This holesky.json contains invalid config values. This test ensues that those config values are
        // ignored for the correct ones contained in another holesky.json file embedded in the config directory
        [TestCase("holesky.json", 0x4268UL)]
        public void ignoring_custom_chainSpec_when_embedded_exists(string chainSpecPath, ulong chainId)
        {
            _loader.LoadEmbeddedOrFromFile(chainSpecPath).Should()
                .Match<ChainSpec>(cs => cs.ChainId == chainId);
        }

        [TestCase("chainspec/custom_chainspec_that_does_not_exist.json")]
        public void ChainSpecNotFound(string chainSpecPath)
        {
            var tryLoad = () => _loader.LoadEmbeddedOrFromFile(chainSpecPath);
            tryLoad.Should().Throw<FileNotFoundException>();
        }

        [TestCase("chainspec/arena-z-mainnet.json.zst", 7897UL)]
        public void Zstandard_Compressed_ChainSpec(string chainSpecPath, ulong chainId)
        {
            _loader.LoadEmbeddedOrFromFile(chainSpecPath).Should()
                .Match<ChainSpec>(cs => cs.ChainId == chainId);
        }
    }
}
