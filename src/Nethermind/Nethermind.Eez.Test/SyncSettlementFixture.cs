// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Test;

/// <summary>
/// A two-block window whose settling block carries one outbound call and one inbound delivery, laid out the way
/// the composer lays out a Sync block: <c>[load, user, delivery]</c>.
/// </summary>
public sealed class SyncSettlementFixture
{
    public const ulong ChainId = 6290;
    public const ulong RollupId = 1;
    public const ulong StartingNonce = 5;

    public static readonly Address L1Target = new("0x00000000000000000000000000000000000000dd");
    public static readonly Address L1Sender = new("0x00000000000000000000000000000000000000aa");
    public static readonly Address L2Contract = new("0x00000000000000000000000000000000000000bb");
    public static readonly Address Beneficiary = new("0x1111111111111111111111111111111111111111");

    public SyncSettlementFixture()
    {
        OutboundCall = new CrossChainCall(0, false, 0, L2Contract, RollupId, L1Target, 3, [4, 5]);
        OutboundDaEntry = new ExecutionEntry([], default, [OutboundCall], [], default, RollupId, true, [0xee]);
        InboundAction = new DaAction(0, RollupId, L1Sender, L2Contract, 7, 0, [1, 2, 3], true, [0xab]);

        Transaction user = Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        UserTransaction = Encode(user);
        SyncTransactions = SyncBlock.BuildTransactions([(OutboundDaEntry, UserTransaction)], [InboundAction.ToEntry(RollupId)], ChainId, RollupId, StartingNonce);
        Transaction[] settlingTransactions = Array.ConvertAll(SyncTransactions, Decode);
        settlingTransactions[1].SenderAddress = user.SenderAddress;

        Transaction preceding = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        PrecedingTransaction = Encode(preceding);
        PrecedingBlock = Build.A.Block.WithNumber(1).WithBeneficiary(Beneficiary).WithExtraData([]).WithTransactions(preceding).TestObject;
        Settling = Build.A.Block.WithNumber(2).WithParent(PrecedingBlock).WithBeneficiary(Beneficiary).WithExtraData([9])
            .WithTransactions(settlingTransactions).TestObject;

        Observation = InboundDelivery.Inspect(settlingTransactions[2].Value, settlingTransactions[2].Data.Span, true, RollupId);
    }

    public CrossChainCall OutboundCall { get; }

    public ExecutionEntry OutboundDaEntry { get; }

    public DaAction InboundAction { get; }

    public byte[] UserTransaction { get; }

    public byte[] PrecedingTransaction { get; }

    public byte[][] SyncTransactions { get; }

    public Block PrecedingBlock { get; }

    public Block Settling { get; }

    public InboundObservation Observation { get; }

    /// <summary>The call hash of the outbound call's event.</summary>
    public static ValueHash256 EventCallHash => CrossChainCallHash.Compute(false, L2Contract, RollupId, L1Target, 0, 3, 0, [4, 5]);

    public Block[] Window => [PrecedingBlock, Settling];

    /// <summary>The outbound entry's rolling hash up to its call's begin, which DA's result closes.</summary>
    public static ValueHash256 PendingRollingHash => Keccak.Compute("outbound call begin").ValueHash256;

    public AuthorizedOutbound[] Outbound =>
        [new(0, 1, OutboundDaEntry with { ReturnData = [] }, PendingRollingHash, RollingHash.CallEnd(PendingRollingHash, true, OutboundDaEntry.ReturnData))];

    public AuthorizedInbound[] Inbound => [new(2, Observation)];

    public DaAction[] Actions => [DaAction.FromEntry(OutboundDaEntry, RollupId), InboundAction];

    public byte[] Payload(DaAction[]? actions = null, byte[][]? settlingPublished = null, byte[]? settlingExtraData = null) =>
        DaPayloadCodec.Encode(RollupId,
        [
            new DaBlock(Beneficiary, [], [PrecedingTransaction]),
            new DaBlock(Beneficiary, settlingExtraData ?? [9], settlingPublished ?? [UserTransaction]),
        ], actions ?? Actions);

    public TxReceipt[] SettlingReceipts(byte loadStatus = StatusCode.Success, byte deliveryStatus = StatusCode.Success, LogEntry? userLog = null) =>
    [
        new() { StatusCode = loadStatus, Logs = [] },
        new() { StatusCode = StatusCode.Success, Logs = [userLog ?? EventLog(EventCallHash)] },
        new() { StatusCode = deliveryStatus, Logs = [] },
    ];

    /// <summary>The L1 entries the batch claims for the two effects, chained after an anchor ending at <paramref name="anchorEnd"/>.</summary>
    public (ExecutionEntry Outbound, RollupUpdate OutboundUpdate, ExecutionEntry Inbound, RollupUpdate InboundUpdate) ClaimedEntries(in ValueHash256 anchorEnd,
        in ValueHash256 outboundEnd, in ValueHash256 inboundEnd)
    {
        RollupUpdate outboundUpdate = new(RollupId, anchorEnd, outboundEnd, new Int256.Int256(-3));
        ValueHash256 outboundRolling = RollingHash.CallEnd(
            RollingHash.CallBegin(RollingHash.SeedL1([new StateCommitment(RollupId, anchorEnd)], default), EventCallHash), true, OutboundDaEntry.ReturnData);
        ExecutionEntry outbound = OutboundDaEntry with { RollupUpdates = [outboundUpdate], RollingHash = outboundRolling, ReturnData = [] };

        RollupUpdate inboundUpdate = new(RollupId, outboundEnd, inboundEnd, new Int256.Int256(7));
        ValueHash256 inboundRolling = RollingHash.SeedL1([new StateCommitment(RollupId, outboundEnd)], Observation.CallHash);
        ExecutionEntry inbound = new([inboundUpdate], Observation.CallHash, [], [], inboundRolling, RollupId, true, Observation.ReturnData);
        return (outbound, outboundUpdate, inbound, inboundUpdate);
    }

    public static LogEntry EventLog(in ValueHash256 callHash, int topicCount = 3, byte[]? data = null, Address? emitter = null)
    {
        byte[] proxy = new byte[32];
        L2Contract.Bytes.CopyTo(proxy.AsSpan(12));
        Hash256[] topics = [OutboundEvent.Signature, new Hash256(callHash), new Hash256(proxy)];
        return new LogEntry(emitter ?? EezConstants.Eezl2Address, data ?? EventData(0), topics[..topicCount]);
    }

    public static byte[] EventData(ulong callGas)
    {
        byte[] data = new byte[6 * 32];
        L2Contract.Bytes.CopyTo(data.AsSpan(12, 20));
        data[63] = 0x80;
        new UInt256(3).ToBigEndian(data.AsSpan(64, 32));
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(120, 8), callGas);
        data[159] = 2;
        data[160] = 4;
        data[161] = 5;
        return data;
    }

    public static byte[] Encode(Transaction transaction) => TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;

    public static Transaction Decode(byte[] encoded) => TxDecoder.Instance.Decode(encoded, RlpBehaviors.SkipTypedWrapping)!;
}
