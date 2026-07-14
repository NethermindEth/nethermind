// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;
using Nethermind.Stateless.Execution;
using Nethermind.Stateless.Execution.IO;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test.ZkEvmFixtures;

public class ZkEvmBlockchainTests : ZkEvmBlockchainTestFixture;

public abstract class ZkEvmBlockchainTestFixture : PyspecLinuxX64BlockchainFixture
{
    protected ZkEvmBlockchainTestFixture() : base(parallel: false, batchRead: false) { }

    private static readonly Lazy<IReadOnlyList<BlockchainTest>> _tests = new(() =>
        ZkEvmMutatedWitnessIndex.StampMutatedBlocks(
            new TestsSourceLoader(
                new LoadPyspecTestsStrategy { ArchiveVersion = Constants.ArchiveVersion, ArchiveName = Constants.ArchiveName },
                "fixtures/blockchain_tests").LoadTests<BlockchainTest>()).ToList());

    [TestCaseSource(nameof(LoadWitnessTests))]
    public async Task WitnessMatchesFixture(BlockchainTest test) => Assert.That((await RunTest(test)).Pass, Is.True);

    [TestCaseSource(nameof(LoadStatelessTests))]
    public void StatelessExecutorOutputMatchesFixture(string inputBytes, string expectedOutputBytes)
    {
        if (!inputBytes.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"StatelessInputBytes must be 0x-prefixed.");

        if (!expectedOutputBytes.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"StatelessOutputBytes must be 0x-prefixed.");

        byte[] actualOutput = StatelessExecutor.Execute(Convert.FromHexString(inputBytes[2..]));
        byte[] expectedOutput = Convert.FromHexString(expectedOutputBytes[2..]);

        Assert.That(actualOutput, Is.EqualTo(expectedOutput),
            $"Expected {expectedOutput.ToHexString(true)}, got {actualOutput.ToHexString(true)}");
    }

    private static IEnumerable<TestCaseData> LoadWitnessTests() => PyspecLoader.ToTestCases(_tests.Value);

    private static IEnumerable<TestCaseData> LoadStatelessTests()
    {
        foreach (BlockchainTest test in _tests.Value)
        {
            if (test.Blocks is not { Length: > 0 } blocks)
                continue;

            for (int i = 0; i < blocks.Length; i++)
            {
                TestBlockJson block = blocks[i];

                if (block.StatelessInputBytes is null && block.StatelessOutputBytes is null)
                    continue;

                if (block.StatelessInputBytes is null || block.StatelessOutputBytes is null)
                    throw new InvalidDataException($"Incomplete stateless fixture data in {test.Name}, block {i}.");

                yield return new TestCaseData(block.StatelessInputBytes, block.StatelessOutputBytes)
                    .SetName($"{test.Name}_stateless_block_{i}");
            }
        }
    }
}

[TestFixture]
public class StatelessSchemaTests
{
    [TestCase(10UL, 20UL, true)]
    [TestCase(9UL, 20UL, false)]
    [TestCase(10UL, 19UL, false)]
    public void Every_fork_activation_bound_must_be_active(ulong blockNumber, ulong timestamp, bool expected)
    {
        SszForkActivation activation = new() { BlockNumber = [10], Timestamp = [20] };

        Assert.That(activation.IsActive(CreateHeader(blockNumber, timestamp)), Is.EqualTo(expected));
    }

    [Test]
    public void Fork_activation_requires_at_least_one_bound()
    {
        SszForkActivation activation = new() { BlockNumber = [], Timestamp = [] };

        Assert.That(() => activation.IsActive(CreateHeader(10, 20)), Throws.TypeOf<InvalidDataException>());
    }

    [TestCase(BlockchainIds.Sepolia, false)]
    [TestCase(BlockchainIds.Gnosis, true)]
    [TestCase(BlockchainIds.Chiado, true)]
    public void Amsterdam_schema_uses_chain_appropriate_fork_catalog(ulong chainId, bool usesGnosisRules)
    {
        IForkAwareSpecProvider baseProvider = chainId switch
        {
            BlockchainIds.Sepolia => SepoliaSpecProvider.Instance,
            BlockchainIds.Gnosis => GnosisSpecProvider.Instance,
            BlockchainIds.Chiado => ChiadoSpecProvider.Instance,
            _ => throw new AssertionException($"Unsupported test chain: {chainId}")
        };
        ForkConfig forkConfig = new()
        {
            Activation = new SszForkActivation { BlockNumber = [], Timestamp = [20] }
        };

        ISpecProvider provider = StatelessSpecProvider.Create(baseProvider, chainId, forkConfig, ProtocolFork.Amsterdam);
        IReleaseSpec spec = provider.GetSpec(new ForkActivation(1, 20));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.ChainId, Is.EqualTo(chainId));
            Assert.That(spec.Name, Is.EqualTo(Amsterdam.Instance.Name));
            Assert.That(spec, usesGnosisRules ? Is.SameAs(AmsterdamGnosis.Instance) : Is.SameAs(Amsterdam.Instance));
        }
    }

    private static BlockHeader CreateHeader(ulong blockNumber, ulong timestamp) => new(
        Hash256.Zero,
        Hash256.Zero,
        Address.Zero,
        UInt256.Zero,
        blockNumber,
        0,
        timestamp,
        []);
}
