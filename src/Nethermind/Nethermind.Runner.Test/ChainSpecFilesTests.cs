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

        private readonly IJsonSerializer _jsonSerializer = new EthereumJsonSerializer();
        private readonly IChainSpecLoader _loader;
        private readonly ILogger _logger;
        public ChainSpecFilesTests()
        {
            _loader = new ChainSpecLoader(_jsonSerializer);
            _logger = default;
        }

        [TestCase("foundation", 1UL)]
        [TestCase("chainspec/foundation", 1UL)]
        [TestCase("chainspec/foundation.json", 1UL)]
        public void different_formats_to_chainSpecPath(string chainSpecPath, ulong chainId)
        {
            _loader.LoadEmbeddedOrFromFile(chainSpecPath, _logger).Should()
                .Match<ChainSpec>(cs => cs.ChainId == chainId);
        }

        [TestCase("testspec.json", 0x55UL)]
        public void ChainSpec_from_file(string chainSpecPath, ulong chainId)
        {
            _loader.LoadEmbeddedOrFromFile(chainSpecPath, _logger).Should()
                .Match<ChainSpec>(cs => cs.ChainId == chainId);
        }

        [TestCase("holesky.json", 0x4268UL)]
        public void ignoring_custom_chainSpec_when_embedded_exists(string chainSpecPath, ulong chainId)
        {
            _loader.LoadEmbeddedOrFromFile(chainSpecPath, _logger).Should()
                .Match<ChainSpec>(cs => cs.ChainId == chainId);
        }

        [TestCase("chainspec/custom_chainspec_that_does_not_exist.json")]
        public void ChainSpecNotFound(string chainSpecPath)
        {
            _loader.Invoking(l => l.LoadEmbeddedOrFromFile(chainSpecPath, _logger))
                .Should().Throw<FileNotFoundException>();
        }

    }
}
