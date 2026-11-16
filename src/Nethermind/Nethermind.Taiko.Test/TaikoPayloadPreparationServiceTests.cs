// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Taiko.TaikoSpec;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

public class TaikoPayloadPreparationServiceTests
{
    [Test]
    public async Task StartPreparingPayload_repeated_attributes_keep_built_block_hash_in_l1_origin()
    {
        L1OriginStore l1OriginStore = new(new TestMemDb(), new L1OriginDecoder());
        TaikoPayloadPreparationService service = new(
            ProcessorReturningSuggestedBlock(),
            WorldStateWithEveryState(),
            l1OriginStore,
            TaikoSpecProvider(),
            LimboLogs.Instance,
            TxDecoder.Instance);
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).TestObject;

        string? firstId = service.StartPreparingPayload(parent, BuildAttributes());
        ValueHash256? builtBlockHash = l1OriginStore.ReadL1Origin(1)!.L2BlockHash;

        // The driver resends the same attributes, deserialized afresh with the zero L2 block hash it always sends.
        // Its L1 fields differ from the first call's, so the stored record shows whether the update path rewrote L1Origin.
        TaikoPayloadAttributes resent = BuildAttributes();
        resent.L1Origin = new L1Origin(1, Keccak.Zero, 6, TestItem.KeccakD, null);
        string? secondId = service.StartPreparingPayload(parent, resent);
        // GetPayload removes the payload, so it is read only after the second call.
        Block built = (await service.GetPayload(firstId!))!.CurrentBestBlock!;

        L1Origin stored = l1OriginStore.ReadL1Origin(1)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondId, Is.EqualTo(firstId));
            Assert.That(builtBlockHash, Is.EqualTo(built.Hash!.ValueHash256));
            Assert.That(stored.L2BlockHash, Is.EqualTo(builtBlockHash));
            Assert.That(stored.L1BlockHeight, Is.EqualTo(6));
            Assert.That(stored.L1BlockHash, Is.EqualTo(TestItem.KeccakD.ValueHash256));
        }
    }

    [Test]
    public async Task StartPreparingPayload_undecodable_proposed_tx_is_left_out_of_built_block()
    {
        Transaction first = Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction second = Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        TaikoPayloadAttributes attributes = BuildAttributes();
        attributes.BlockMetadata!.TxList = EncodeTxList(Rlp.Encode(first), Rlp.Encode(Build.A.Transaction.TestObject), Rlp.Encode(second));

        TaikoPayloadPreparationService service = CreateService();

        string? payloadId = service.StartPreparingPayload(Build.A.BlockHeader.WithNumber(0).TestObject, attributes);
        Block built = (await service.GetPayload(payloadId!))!.CurrentBestBlock!;

        Assert.That(built.Transactions.Select(static tx => tx.Hash), Is.EqualTo(new[] { first.Hash, second.Hash }), "only the undecodable entry should be left out");
    }

    [Test]
    public void StartPreparingPayload_undecodable_first_tx_throws()
    {
        Transaction second = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        TaikoPayloadAttributes attributes = BuildAttributes();
        attributes.BlockMetadata!.TxList = EncodeTxList(Rlp.Encode(Build.A.Transaction.TestObject), Rlp.Encode(second));

        Assert.Throws<RlpReaderException>(() => CreateService().StartPreparingPayload(Build.A.BlockHeader.WithNumber(0).TestObject, attributes));
    }

    private static TaikoPayloadPreparationService CreateService() => new(
        ProcessorReturningSuggestedBlock(),
        WorldStateWithEveryState(),
        new L1OriginStore(new TestMemDb(), new L1OriginDecoder()),
        TaikoSpecProvider(),
        LimboLogs.Instance,
        TxDecoder.Instance);

    private static byte[] EncodeTxList(params byte[][] items)
    {
        int contentLength = items.Sum(static item => item.Length);
        byte[] list = new byte[Rlp.LengthOfSequence(contentLength)];
        int position = Rlp.StartSequence(list, 0, contentLength);
        foreach (byte[] item in items)
        {
            item.CopyTo(list, position);
            position += item.Length;
        }

        return list;
    }

    private static TaikoPayloadAttributes BuildAttributes() => new()
    {
        Timestamp = 1_700_000_000,
        PrevRandao = TestItem.KeccakB,
        SuggestedFeeRecipient = TestItem.AddressA,
        Withdrawals = [],
        BaseFeePerGas = 1,
        BlockMetadata = new BlockMetadata
        {
            Beneficiary = TestItem.AddressA,
            GasLimit = 16_000_000,
            Timestamp = 1_700_000_000,
            MixHash = TestItem.KeccakB,
            BatchID = 7,
            TxList = [0xc0],
            ExtraData = [0x4b, 0x00],
        },
        L1Origin = new L1Origin(1, Keccak.Zero, 5, TestItem.KeccakC, null),
    };

    private static IBlockchainProcessor ProcessorReturningSuggestedBlock()
    {
        IBlockchainProcessor processor = Substitute.For<IBlockchainProcessor>();
        processor.Process(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                Block block = callInfo.Arg<Block>();
                block.Header.Hash = block.Header.CalculateHash();
                return block;
            });
        return processor;
    }

    private static IWorldState WorldStateWithEveryState()
    {
        IWorldState worldState = Substitute.For<IWorldState>();
        worldState.HasStateForTargetBlock(Arg.Any<BlockHeader>()).Returns(true);
        return worldState;
    }

    private static ISpecProvider TaikoSpecProvider()
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Substitute.For<ITaikoReleaseSpec>());
        return specProvider;
    }
}
