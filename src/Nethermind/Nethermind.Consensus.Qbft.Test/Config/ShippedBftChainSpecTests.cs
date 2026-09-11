// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Config;

/// <summary>
/// Each shipped BFT chainspec selects its engine, carries the network's bootnodes and reproduces the
/// genesis hash the live network reports.
/// </summary>
/// <remarks>
/// The expected hashes were read from each network's own JSON-RPC, <c>eth_getBlockByNumber</c> at
/// block 0, so a mismatch means the chainspec would fork away from the live chain at genesis. Engine
/// settings are asserted through <see cref="BftForksSchedule"/> because that, rather than the parsed
/// parameters, is what consensus reads.
/// </remarks>
public class ShippedBftChainSpecTests
{
    /// <param name="File">Chainspec file name under <c>Chains/</c>.</param>
    /// <param name="SealEngine">Expected <see cref="SealEngineType"/>.</param>
    /// <param name="Bootnodes">Enodes the chainspec must publish; these chains have no DNS discovery.</param>
    /// <param name="GenesisHash">Hash of block 0 as reported by the live network.</param>
    public sealed record Chain(
        string File,
        string SealEngine,
        ulong ChainId,
        int BlockPeriodSeconds,
        long EpochLength,
        int RequestTimeoutSeconds,
        int Bootnodes,
        string GenesisHash)
    {
        public override string ToString() => File;
    }

    private static readonly Chain[] _chains =
    [
        new("rbb.json", SealEngineType.Qbft, 12_120_014, 4, 30_000, 8, 7,
            "0xa7ce1b4328b704f45901db430f4b52eb1bc633cf78b8bde63c60e0ee84c431ed"),
        new("kalychain.json", SealEngineType.Qbft, 3888, 2, 28_800, 4, 2,
            "0x1358eef47fa2ef9d7703fb01589472779a627f342150a65bd0388bdb256ff654"),
        new("alastria.json", SealEngineType.Ibft2, 2020, 1, 30_000, 10, 69,
            "0x43c3368fd903b2d1781177b37f596b946441a68562ac0929f7377c00fd2966a3"),
        new("lacchain.json", SealEngineType.Ibft2, 648_541, 2, 150_000, 4, 4,
            "0xf7c3c99a9ccb707bb8db98aa446f5158beb02eeeb04a7a08af2fa63f89b437aa"),
        new("lacchain-protestnet.json", SealEngineType.Ibft2, 648_540, 2, 150_000, 4, 2,
            "0x67e642b36170d8df42a7c2a62576eee6b2bf05f1138ce7f093669816032fe6bc"),
    ];

    private static Chain Named(string file) => Array.Find(_chains, c => c.File == file)!;

    private static ChainSpec Load(string file) => Load(Named(file));

    private static ChainSpec Load(Chain chain)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains", chain.File);
        return new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance).LoadEmbeddedOrFromFile(path);
    }

    private static IBftExtraDataCodecSelector CodecFor(ChainSpec chainSpec) =>
        chainSpec.SealEngineType == SealEngineType.Qbft ? QbftOnlyCodecSelector.Instance : Ibft2OnlyCodecSelector.Instance;

    /// <summary>Reads the engine settings the way the plugin's registration does, for whichever engine applies.</summary>
    private static (BftForksSchedule Schedule, long EpochLength) LoadEngine(ChainSpec chainSpec)
    {
        if (chainSpec.SealEngineType == SealEngineType.Qbft)
        {
            QbftChainSpecEngineParameters qbft = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<QbftChainSpecEngineParameters>();
            return (BftForksSchedule.Create(qbft, ulong.MaxValue), qbft.EpochLength);
        }

        Ibft2ChainSpecEngineParameters ibft2 = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<Ibft2ChainSpecEngineParameters>();
        return (BftForksSchedule.Create(ibft2, ulong.MaxValue), ibft2.EpochLength);
    }

    [TestCaseSource(nameof(_chains))]
    public void SelectsTheEngineAndItsSettings(Chain chain)
    {
        ChainSpec chainSpec = Load(chain);
        (BftForksSchedule schedule, long epochLength) = LoadEngine(chainSpec);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.SealEngineType, Is.EqualTo(chain.SealEngine));
            Assert.That(chainSpec.ChainId, Is.EqualTo(chain.ChainId));
            Assert.That(chainSpec.NetworkId, Is.EqualTo(chain.ChainId));
            Assert.That(schedule.GetFork(0, 0).BlockPeriodSeconds, Is.EqualTo(chain.BlockPeriodSeconds));
            Assert.That(schedule.GetFork(0, 0).RequestTimeoutSeconds, Is.EqualTo(chain.RequestTimeoutSeconds));
            Assert.That(epochLength, Is.EqualTo(chain.EpochLength));
            Assert.That(chainSpec.Parameters.MaximumExtraDataSize, Is.EqualTo(int.MaxValue), "BFT extra data carries the validator list and seals");
            Assert.That(chainSpec.Bootnodes, Has.Length.EqualTo(chain.Bootnodes));
        }
    }

    [TestCaseSource(nameof(_chains))]
    public void GenesisExtraDataListsTheInitialValidators(Chain chain)
    {
        ChainSpec chainSpec = Load(chain);
        BftExtraData extraData = CodecFor(chainSpec).ForBlock(0).Decode(chainSpec.Genesis!.Header.ExtraData);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(extraData.Validators, Is.Not.Empty);
            Assert.That(extraData.Seals, Is.Empty, "genesis carries no commit seals");
            Assert.That(extraData.Round, Is.EqualTo(0));
            Assert.That(extraData.Vote, Is.Null);
        }
    }

    [TestCaseSource(nameof(_chains))]
    public void GenesisHashMatchesTheLiveNetwork(Chain chain)
    {
        ChainSpec chainSpec = Load(chain);
        ChainSpecBasedSpecProvider specProvider = new(chainSpec);
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);
        GenesisBuilder inner = new(chainSpec, specProvider, worldState, Substitute.For<ITransactionProcessor>());
        Block genesis = new BftGenesisBuilder(inner, CodecFor(chainSpec)).Build();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(genesis.Header, Is.InstanceOf<BftBlockHeader>());
            Assert.That(genesis.Hash, Is.EqualTo(new Hash256(chain.GenesisHash)));
        }
    }

    /// <summary>KalyChain schedules 29 block-reward halvings, so its rewards have to come from the schedule.</summary>
    [Test]
    public void KalyChainBlockRewardHalvingsAreScheduled()
    {
        BftForksSchedule schedule = LoadEngine(Load("kalychain.json")).Schedule;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.GetFork(0, 0).BlockReward, Is.EqualTo(UInt256.Parse("3000000000000000000")), "decimal blockreward in the genesis qbft section");
            Assert.That(schedule.GetFork(9_434_167, 0).BlockReward, Is.EqualTo(UInt256.Parse("3000000000000000000")));
            Assert.That(schedule.GetFork(9_434_168, 0).BlockReward, Is.EqualTo(UInt256.Parse("750000000000000000")), "first halving");
            Assert.That(schedule.GetFork(137_280_000, 0).BlockReward, Is.EqualTo(UInt256.Zero), "the last transition ends the emission");

            // A live node following the published genesis diverged on the state root of empty block
            // 51,192,000, which can only be the block reward. The published transitions schedule no
            // change anywhere near it, so the reward is flat across the divergence point and the
            // chain must be running a genesis this file does not describe.
            Assert.That(schedule.GetFork(51_191_999, 0).BlockReward, Is.EqualTo(UInt256.Parse("1464843750000000")));
            Assert.That(schedule.GetFork(51_192_000, 0).BlockReward, Is.EqualTo(UInt256.Parse("1464843750000000")));
        }
    }

    /// <summary>
    /// KalyChain's genesis schedules Paris through Prague by block number. Besu has no block-number
    /// schedule for those forks, and the chain proves it: its head is long past <c>pragueBlock</c> and
    /// still carries no withdrawals root, so they have to stay inactive here too.
    /// </summary>
    [Test]
    public void KalyChainPostMergeForkBlocksStayInactive()
    {
        ChainSpec chainSpec = Load("kalychain.json");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.Parameters.Eip1559Transition, Is.EqualTo(0UL), "londonBlock 0");
            Assert.That(chainSpec.Parameters.Eip3855TransitionTimestamp, Is.Null, "shanghaiBlock is not a Besu schedule");
            Assert.That(chainSpec.Parameters.Eip4844TransitionTimestamp, Is.Null, "cancunBlock is not a Besu schedule");
            Assert.That(chainSpec.Parameters.Eip7702TransitionTimestamp, Is.Null, "pragueBlock is not a Besu schedule");
            Assert.That(chainSpec.Parameters.TerminalTotalDifficulty, Is.Null, "parisBlock does not merge the chain");
        }
    }

    /// <summary>
    /// Alastria names no fork before Petersburg, so every earlier fork has to come from Besu's
    /// cumulative schedule, and it shortens its block period part way up the chain.
    /// </summary>
    [Test]
    public void AlastriaInheritsTheForksItOmitsAndSchedulesItsPeriodChange()
    {
        ChainSpec chainSpec = Load("alastria.json");
        BftForksSchedule schedule = LoadEngine(chainSpec).Schedule;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.Parameters.Eip140Transition, Is.EqualTo(0UL), "Byzantium, in force under the declared Petersburg");
            Assert.That(chainSpec.Parameters.Eip1283DisableTransition, Is.EqualTo(0UL), "Petersburg, declared as constantinoplefixblock");
            Assert.That(chainSpec.Parameters.Eip2200Transition, Is.EqualTo(33_253_347UL), "Istanbul");
            Assert.That(chainSpec.Parameters.Eip2929Transition, Is.EqualTo(33_255_747UL), "Berlin");
            Assert.That(chainSpec.Parameters.Eip1559Transition, Is.Null, "London is not declared, so the chain has no base fee");
            Assert.That(schedule.GetFork(32_390_904, 0).BlockPeriodSeconds, Is.EqualTo(1));
            Assert.That(schedule.GetFork(32_390_905, 0).BlockPeriodSeconds, Is.EqualTo(3), "transitions.ibft2");

            // The same transition sets requesttimeoutseconds, which is not one of the eight keys
            // Besu's BftFork reads, so it stays at the genesis value there as it does in Besu.
            Assert.That(schedule.GetFork(32_390_905, 0).RequestTimeoutSeconds, Is.EqualTo(10));
        }
    }

    /// <summary>Both LACChain networks raise the EIP-170 contract size limit.</summary>
    [TestCase("lacchain.json")]
    [TestCase("lacchain-protestnet.json")]
    public void LacChainRaisesTheContractSizeLimit(string file)
    {
        ChainSpec chainSpec = Load(file);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.Parameters.MaxCodeSize, Is.EqualTo(8_000_000L), "contractSizeLimit");
            Assert.That(chainSpec.Parameters.MaxCodeSizeTransition, Is.EqualTo(0UL));
            Assert.That(chainSpec.Parameters.Eip2929Transition, Is.EqualTo(0UL), "berlinBlock 0");
            Assert.That(chainSpec.Parameters.Eip140Transition, Is.EqualTo(0UL), "everything before Berlin is in force with it");
        }
    }
}
