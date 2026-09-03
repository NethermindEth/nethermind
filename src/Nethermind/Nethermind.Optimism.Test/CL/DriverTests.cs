// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.CL.L1Bridge;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class DriverTests
{
    private const ulong L2BlockTime = 2;
    private const ulong FirstBlockNumber = 1;
    private static readonly Hash256 OriginHash = TestItem.KeccakA;

    /// <summary>Span-batch payload of a legacy transaction: <c>rlp(value, gasPrice, data)</c>.</summary>
    private static readonly byte[] ValidTx = Rlp.Encode(Rlp.OfZero, Rlp.OfZero, Rlp.OfEmptyByteArray).Bytes;

    /// <summary>Same shape, but <c>value</c> is a list where a scalar is required, so decoding throws.</summary>
    private static readonly byte[] MalformedTx = Rlp.Encode(Rlp.OfEmptyList, Rlp.OfZero, Rlp.OfEmptyByteArray).Bytes;

    [SetUp]
    public void SetUp() => TxDecoder.Instance.RegisterDecoder(new OptimismTxDecoder<Transaction>());

    private static IEnumerable<TestCaseData> UnderivableBatches()
    {
        yield return new TestCaseData(Batch(OriginHash, (TxType.Blob, ValidTx))) { TestName = "UnknownTxType" };
        yield return new TestCaseData(Batch(OriginHash, (TxType.Legacy, MalformedTx))) { TestName = "MalformedTxData" };
        yield return new TestCaseData(Batch(TestItem.KeccakB, (TxType.Legacy, ValidTx))) { TestName = "L1OriginMismatch" };
    }

    [TestCaseSource(nameof(UnderivableBatches))]
    public async Task Batch_that_cannot_be_derived_does_not_stop_derivation(BatchV1 underivable)
    {
        Driver driver = BuildDriver(out List<ulong> imported, underivable, Batch(OriginHash, (TxType.Legacy, ValidTx)));

        await driver.Run(Timeout);

        Assert.That(imported, Is.EqualTo(new ulong[] { FirstBlockNumber }));
    }

    [Test]
    public async Task Batch_failing_on_a_later_block_imports_nothing()
    {
        BatchV1 secondBlockFails = Batch(OriginHash, (TxType.Legacy, ValidTx), (TxType.Blob, ValidTx));
        Driver driver = BuildDriver(out List<ulong> imported, secondBlockFails);

        await driver.Run(Timeout);

        Assert.That(imported, Is.Empty);
    }

    /// <summary>A batch of one block per supplied transaction, all sharing a single L1 origin.</summary>
    private static BatchV1 Batch(Hash256 originHash, params (TxType Type, byte[] Data)[] txs)
    {
        ulong[] oneTxPerBlock = new ulong[txs.Length];
        TxType[] types = new TxType[txs.Length];
        ReadOnlyMemory<byte>[] data = new ReadOnlyMemory<byte>[txs.Length];
        (UInt256 R, UInt256 S)[] signatures = new (UInt256, UInt256)[txs.Length];
        Address[] tos = new Address[txs.Length];
        for (int i = 0; i < txs.Length; i++)
        {
            oneTxPerBlock[i] = 1;
            types[i] = txs[i].Type;
            data[i] = txs[i].Data;
            signatures[i] = (UInt256.One, UInt256.One);
            tos[i] = TestItem.AddressA;
        }

        return new BatchV1
        {
            RelTimestamp = FirstBlockNumber * L2BlockTime,
            L1OriginNum = 0,
            ParentCheck = new byte[20],
            L1OriginCheck = originHash.Bytes[..20].ToArray(),
            BlockCount = (ulong)txs.Length,
            OriginBits = 0,
            BlockTxCounts = oneTxPerBlock,
            Txs = new BatchV1.Transactions
            {
                ContractCreationBits = 0,
                YParityBits = 0,
                ProtectedBits = 0,
                Signatures = signatures,
                Tos = tos,
                Data = data,
                Types = types,
                TotalLegacyTxCount = (ulong)txs.Length,
                Nonces = new ulong[txs.Length],
                Gases = new ulong[txs.Length],
            }
        };
    }

    /// <remarks>
    /// Drives the real <see cref="DerivationPipeline"/>, which <see cref="Driver"/> constructs itself.
    /// The batch channel is completed up front, so <see cref="Driver.Run"/> returns once the given batches
    /// have been handled and <paramref name="imported"/> can be asserted without polling.
    /// </remarks>
    private static Driver BuildDriver(out List<ulong> imported, params BatchV1[] batches)
    {
        Channel<(BatchV1, ulong)> batchChannel = Channel.CreateUnbounded<(BatchV1, ulong)>();
        foreach (BatchV1 batch in batches) batchChannel.Writer.TryWrite((batch, 0));
        batchChannel.Writer.Complete();

        IDecodingPipeline decodingPipeline = Substitute.For<IDecodingPipeline>();
        decodingPipeline.DecodedBatchesReader.Returns(batchChannel.Reader);

        L1Block origin = new()
        {
            ExtraData = [],
            Hash = OriginHash,
            ParentHash = Hash256.Zero,
            MixHash = Hash256.Zero,
            ParentBeaconBlockRoot = Hash256.Zero,
            Timestamp = 1,
            Number = 0,
            BaseFeePerGas = 1,
            ExcessBlobGas = 0,
        };

        IL1Bridge l1Bridge = Substitute.For<IL1Bridge>();
        // Never completes, so the run loop only reacts to batches.
        l1Bridge.Step(Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource<L1BridgeStepResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        l1Bridge.GetBlock(Arg.Any<ulong>(), Arg.Any<CancellationToken>()).Returns(origin);
        l1Bridge.GetReceiptsByBlockHash(Arg.Any<Hash256>(), Arg.Any<CancellationToken>()).Returns([]);

        IL2Api l2Api = Substitute.For<IL2Api>();
        l2Api.GetBlockByNumber(Arg.Any<ulong>()).Returns(ParentBlock);

        List<ulong> importedBlocks = [];
        imported = importedBlocks;
        IExecutionEngineManager engine = Substitute.For<IExecutionEngineManager>();
        engine.ProcessNewDerivedPayloadAttributes(Arg.Any<PayloadAttributesRef>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                ulong number = call.Arg<PayloadAttributesRef>().Number;
                importedBlocks.Add(number);
                return Task.FromResult<BlockId?>(new BlockId { Number = number, Hash = Hash256.Zero });
            });

        return new Driver(
            l1Bridge,
            decodingPipeline,
            new CLChainSpecEngineParameters
            {
                L2BlockTime = L2BlockTime,
                SystemConfigProxy = TestItem.AddressB,
                OptimismPortalProxy = TestItem.AddressC,
            },
            engine,
            l2Api,
            TestBlockchainIds.ChainId,
            l2GenesisTimestamp: 0,
            LimboLogs.Instance);
    }

    private static L2Block ParentBlock => new()
    {
        Hash = Hash256.Zero,
        ParentHash = Hash256.Zero,
        StateRoot = Keccak.EmptyTreeHash,
        PayloadAttributesRef = new PayloadAttributesRef
        {
            Number = FirstBlockNumber - 1,
            SystemConfig = new SystemConfig { EIP1559Params = new byte[8] },
            L1BlockInfo = L1BlockInfo.Empty,
            PayloadAttributes = new OptimismPayloadAttributes
            {
                PrevRandao = Hash256.Zero,
                SuggestedFeeRecipient = Address.Zero,
                Withdrawals = [],
                Transactions = [],
            }
        }
    };

    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
}
