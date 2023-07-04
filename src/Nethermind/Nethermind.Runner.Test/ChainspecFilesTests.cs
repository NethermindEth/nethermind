// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using FluentAssertions;
using Nethermind.Logging;
using NUnit.Framework;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Serialization.Json;
using NSubstitute.ExceptionExtensions;

namespace Nethermind.Runner.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class ChainspecFilesTests
    {

        private IJsonSerializer _jsonSerializer = new EthereumJsonSerializer();
        private IChainSpecLoader _loader;
        public ILogger _logger;
        public ChainspecFilesTests()
        {
            _loader = new ChainSpecLoader(_jsonSerializer);
            _logger = NSubstitute.Substitute.For<ILogger>();
        }

        [TestCase("foundation", 1UL)]
        [TestCase("chainspec/foundation", 1UL)]
        [TestCase("chainspec/foundation.json", 1UL)]
        public void different_formats_to_chainspecPath(string chainspecPath, ulong chainId)
        {
            _loader.LoadEmbeddedOrFromFile(chainspecPath, _logger).Should()
                .Match<ChainSpec>(cs => cs.ChainId == chainId);
        }

        [TestCase("testspec.json", 5UL)]
        public void Chainspec_from_file(string chainspecPath, ulong chainId)
        {
            _loader.LoadEmbeddedOrFromFile(chainspecPath, _logger).Should()
                .Match<ChainSpec>(cs => cs.ChainId == chainId);
        }

        [TestCase("goerli.json", 5UL)]
        public void ignoring_custom_chainspec_when_embedded_exists(string chainspecPath, ulong chainId)
        {
            _loader.LoadEmbeddedOrFromFile(chainspecPath, _logger).Should()
                .Match<ChainSpec>(cs => cs.ChainId == chainId);
        }

        [TestCase("chainspec/custom_chainspec_that_does_not_exist.json")]
        public void ChainspecNotFound(string chainspecPath)
        {
            _loader.Invoking(l => l.LoadEmbeddedOrFromFile(chainspecPath, _logger))
                .Should().Throw<FileNotFoundException>();
        }

    }
}
