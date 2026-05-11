// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Test.SszRest;

/// <summary>
/// Shared payload factories for SSZ codec and middleware tests.
/// Each factory chains from the previous one to avoid repeating field assignments.
/// </summary>
internal static class SszTestData
{
    private static T WithBaseFields<T>(T ep) where T : ExecutionPayload
    {
        ep.ParentHash = TestItem.KeccakA;
        ep.FeeRecipient = TestItem.AddressA;
        ep.StateRoot = TestItem.KeccakB;
        ep.ReceiptsRoot = TestItem.KeccakC;
        ep.LogsBloom = Bloom.Empty;
        ep.PrevRandao = TestItem.KeccakD;
        ep.BlockNumber = 1;
        ep.GasLimit = 1_000_000;
        ep.GasUsed = 0;
        ep.Timestamp = 1_700_000_000;
        ep.ExtraData = [];
        ep.BaseFeePerGas = 1;
        ep.BlockHash = TestItem.KeccakE;
        ep.Transactions = [];
        return ep;
    }

    private static T WithV3Fields<T>(T ep) where T : ExecutionPayloadV3
    {
        WithBaseFields(ep);
        ep.BlockNumber = 100;
        ep.GasLimit = 2_000_000;
        ep.GasUsed = 50_000;
        ep.Timestamp = 1_700_000_100;
        ep.BaseFeePerGas = 10;
        ep.Withdrawals = [];
        ep.BlobGasUsed = 0x20000;
        ep.ExcessBlobGas = 0x40000;
        return ep;
    }

    public static ExecutionPayload MakeMinimalPayload() =>
        WithBaseFields(new ExecutionPayload());

    public static ExecutionPayloadV3 MakeV3Payload() =>
        WithV3Fields(new ExecutionPayloadV3());

    public static ExecutionPayloadV4 MakeV4Payload(byte[] blockAccessList, ulong slotNumber)
    {
        ExecutionPayloadV4 ep = WithV3Fields(new ExecutionPayloadV4());
        ep.BlockAccessList = blockAccessList;
        ep.SlotNumber = slotNumber;
        return ep;
    }
}
