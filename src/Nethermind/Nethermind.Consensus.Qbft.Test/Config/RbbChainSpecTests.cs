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
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Config;

/// <summary>The shipped Rede Blockchain Brasil chainspec parses into QBFT parameters and reproduces the network's genesis hash.</summary>
public class RbbChainSpecTests
{
    private static readonly Hash256 RbbGenesisHash = new("0xa7ce1b4328b704f45901db430f4b52eb1bc633cf78b8bde63c60e0ee84c431ed");

    private static ChainSpec LoadRbb()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/rbb.json");
        return new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance).LoadEmbeddedOrFromFile(path);
    }

    [Test]
    public void ParsesQbftEngineParameters()
    {
        ChainSpec chainSpec = LoadRbb();
        QbftChainSpecEngineParameters parameters = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<QbftChainSpecEngineParameters>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Qbft));
            Assert.That(chainSpec.ChainId, Is.EqualTo(12120014UL), "decimal chainId from the Besu genesis (0xb8efce)");
            Assert.That(parameters.BlockPeriodSeconds, Is.EqualTo(4));
            Assert.That(parameters.EpochLength, Is.EqualTo(30_000));
            Assert.That(parameters.RequestTimeoutSeconds, Is.EqualTo(8));
            Assert.That(parameters.IsValidatorContractMode, Is.False);
            Assert.That(chainSpec.Parameters.MaximumExtraDataSize, Is.EqualTo(int.MaxValue), "BFT extra data carries the validator list and seals");
            Assert.That(chainSpec.Bootnodes, Has.Length.EqualTo(7));
        }
    }

    [Test]
    public void GenesisExtraDataListsTheInitialValidators()
    {
        ChainSpec chainSpec = LoadRbb();
        BftExtraData extraData = QbftExtraDataCodec.Instance.Decode(chainSpec.Genesis!.Header.ExtraData);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(extraData.Validators, Is.Not.Empty);
            Assert.That(extraData.Seals, Is.Empty);
            Assert.That(extraData.Round, Is.EqualTo(0));
            Assert.That(extraData.Vote, Is.Null);
        }
    }

    [Test]
    public void GenesisHashMatchesTheLiveNetwork()
    {
        ChainSpec chainSpec = LoadRbb();
        ChainSpecBasedSpecProvider specProvider = new(chainSpec);
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);
        GenesisBuilder inner = new(chainSpec, specProvider, worldState, Substitute.For<ITransactionProcessor>());
        Block genesis = new QbftGenesisBuilder(inner, QbftOnlyCodecSelector.Instance).Build();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(genesis.Header, Is.InstanceOf<QbftBlockHeader>());
            Assert.That(genesis.Hash, Is.EqualTo(RbbGenesisHash));
        }
    }
}
