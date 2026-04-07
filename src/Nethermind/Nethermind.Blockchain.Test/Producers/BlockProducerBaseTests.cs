// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Evm.State;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers;

public partial class BlockProducerBaseTests
{
    private static readonly Address RefundingContractAddress = TestItem.AddressD;
    private static readonly byte[] RefundingContractCode = Prepare.EvmCode
        .PushData(0)
        .PushData(0)
        .Op(Instruction.SSTORE)
        .Done;

    private class ProducerUnderTest(
        ITxSource txSource,
        IBlockchainProcessor processor,
        ISealer sealer,
        IBlockTree blockTree,
        IWorldState stateProvider,
        IGasLimitCalculator gasLimitCalculator,
        ITimestamper timestamper,
        ILogManager logManager,
        IBlocksConfig blocksConfig)
        : BlockProducerBase(txSource,
            processor,
            sealer,
            blockTree,
            stateProvider,
            gasLimitCalculator,
            timestamper,
            MainnetSpecProvider.Instance,
            logManager,
            new TimestampDifficultyCalculator(),
            blocksConfig)
    {
        public Block Prepare() => PrepareBlock(Build.A.BlockHeader.TestObject);

        public Block Prepare(BlockHeader header) => PrepareBlock(header);

        private class TimestampDifficultyCalculator : IDifficultyCalculator
        {
            public UInt256 Calculate(BlockHeader header, BlockHeader parent) => header.Timestamp;
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Time_passing_does_not_break_the_block()
    {
        ITimestamper timestamper = new IncrementalTimestamper();
        IBlocksConfig blocksConfig = new BlocksConfig();
        ProducerUnderTest producerUnderTest = new(
            EmptyTxSource.Instance,
            Substitute.For<IBlockchainProcessor>(),
            NullSealEngine.Instance,
            Build.A.BlockTree().TestObject,
            Substitute.For<IWorldState>(),
            Substitute.For<IGasLimitCalculator>(),
            timestamper,
            LimboLogs.Instance,
            blocksConfig
            );

        Block block = producerUnderTest.Prepare();
        new UInt256(block.Timestamp).Should().BeEquivalentTo(block.Difficulty);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Parent_timestamp_is_used_consistently()
    {
        ITimestamper timestamper = new IncrementalTimestamper(DateTime.UnixEpoch, TimeSpan.FromSeconds(1));
        IBlocksConfig blocksConfig = new BlocksConfig();

        ProducerUnderTest producerUnderTest = new(
            EmptyTxSource.Instance,
            Substitute.For<IBlockchainProcessor>(),
            NullSealEngine.Instance,
            Build.A.BlockTree().TestObject,
            Substitute.For<IWorldState>(),
            Substitute.For<IGasLimitCalculator>(),
            timestamper,
            LimboLogs.Instance,
            blocksConfig);

        ulong futureTime = UnixTime.FromSeconds(TimeSpan.FromDays(1).TotalSeconds).Seconds;
        Block block = producerUnderTest.Prepare(Build.A.BlockHeader.WithTimestamp(futureTime).TestObject);
        new UInt256(block.Timestamp).Should().BeEquivalentTo(block.Difficulty);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Produced_block_round_trips_through_validator_chain_after_prewarm()
    {
        using BasicTestBlockchain producer = await BasicTestBlockchain.Create(ConfigurePragueSpecWithoutBootstrap);
        using BasicTestBlockchain validator = await BasicTestBlockchain.Create(ConfigurePragueSpecWithoutBootstrap);

        Hash256 producerGenesisHash = producer.BlockTree.Genesis!.Hash!;
        producerGenesisHash.Should().Be(validator.BlockTree.Genesis!.Hash!);

        Transaction refunding = BuildRefundingCallTx(0);
        Transaction second = Build.A.Transaction
            .To(TestBlockchain.AccountB)
            .WithNonce(1)
            .WithGasLimit(60_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        Transaction third = Build.A.Transaction
            .To(TestItem.AddressC)
            .WithNonce(0)
            .WithGasLimit(65_000)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;

        producer.TxPool.SubmitTx(refunding, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
        producer.TxPool.SubmitTx(second, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
        producer.TxPool.SubmitTx(third, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);

        BlockHeader parent = producer.BlockTree.Head!.Header;

        Block? prewarmBlock = await producer.BlockProducer.BuildBlock(
            parent,
            payloadAttributes: new FixedGasLimitPayloadAttributes(parent.Timestamp + 1, 250_000));
        Block? rebuiltBlock = await producer.BlockProducer.BuildBlock(
            parent,
            payloadAttributes: new FixedGasLimitPayloadAttributes(parent.Timestamp + 2, 110_000));

        prewarmBlock.Should().NotBeNull();
        rebuiltBlock.Should().NotBeNull();

        Block decoded = Rlp.Decode<Block>(Rlp.Encode(rebuiltBlock));
        decoded.Header.TotalDifficulty = rebuiltBlock!.TotalDifficulty;
        validator.BlockPreprocessorStep.RecoverData(decoded);

        validator.BlockTree.SuggestBlock(decoded, BlockTreeSuggestOptions.None).Should().Be(AddBlockResult.Added);
        Block? processed = ((BlockchainProcessor)validator.BlockchainProcessor).Process(
            decoded,
            ProcessingOptions.ForceProcessing,
            NullBlockTracer.Instance,
            validator.CancellationToken,
            out string? error);

        processed.Should().NotBeNull(error ?? "the produced block should survive RLP round-trip validation on a second chain");
        prewarmBlock!.Transactions.Length.Should().Be(3);
        rebuiltBlock.Transactions.Length.Should().Be(2);
    }

    private static Transaction BuildRefundingCallTx(ulong nonce)
    {
        return Build.A.Transaction
            .WithNonce(nonce)
            .To(RefundingContractAddress)
            .WithGasLimit(100_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
    }

    private static void ConfigurePragueSpecWithoutBootstrap(ContainerBuilder builder)
    {
        builder.AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(CreatePragueSpec()));
        builder.WithGenesisPostProcessor((_, state, specProvider) =>
        {
            state.CreateAccount(RefundingContractAddress, 1);
            state.InsertCode(RefundingContractAddress, RefundingContractCode, specProvider.GenesisSpec);
            state.Set(new StorageCell(RefundingContractAddress, UInt256.Zero), new byte[] { 1 });
        });
        builder.ConfigureTestConfiguration(cfg => cfg.AddBlockOnStart = false);
    }

    private static IReleaseSpec CreatePragueSpec()
    {
        OverridableReleaseSpec spec = new(Prague.Instance);
        spec.GetType().GetProperty("IsEip7778Enabled")?.SetValue(spec, true);
        return spec;
    }

    private sealed class FixedGasLimitPayloadAttributes : PayloadAttributes
    {
        private readonly long _gasLimit;

        public FixedGasLimitPayloadAttributes(ulong timestamp, long gasLimit)
        {
            Timestamp = timestamp;
            _gasLimit = gasLimit;
        }

        public override long? GetGasLimit() => _gasLimit;
    }
}
