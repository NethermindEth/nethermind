// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Tests for the EIP-8146 block access list sidecar plumbing:
/// <c>engine_notifyBlockAccessListV1</c> and the hash-committed <c>ExecutionPayloadV5</c>.
/// </summary>
[TestFixture]
public class Eip8146Tests
{
    private static byte[] EncodeBal() => Rlp.Encode(
        Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(0, 100))
                .TestObject)
            .TestObject).Bytes;

    [Test]
    public void Notify_stores_sidecar_retrievable_by_keccak_commitment()
    {
        BlockAccessListSidecarStore store = new();
        NotifyBlockAccessListHandler handler = new(store, LimboLogs.Instance);
        byte[] rlp = EncodeBal();

        ResultWrapper<string?> result = handler.Handle(rlp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(store.TryGet(new Hash256(ValueKeccak.Compute(rlp).Bytes), out byte[]? stored), Is.True);
            Assert.That(stored, Is.EqualTo(rlp));
        }
    }

    [Test]
    public void Notify_rejects_malformed_sidecar()
    {
        BlockAccessListSidecarStore store = new();
        NotifyBlockAccessListHandler handler = new(store, LimboLogs.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Handle([0xde, 0xad, 0xbe, 0xef]).Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(handler.Handle([]).Result.ResultType, Is.EqualTo(ResultType.Failure));
        }
    }

    [Test]
    public void ExecutionPayloadV5_carries_the_bal_hash_commitment()
    {
        byte[] rlp = EncodeBal();
        Hash256 balHash = new(ValueKeccak.Compute(rlp).Bytes);
        Block block = Build.A.Block
            .WithSlotNumber(1)
            .TestObject;
        block.Header.BlockAccessListHash = balHash;

        ExecutionPayloadV5 payload = ExecutionPayloadV5.Create(block);
        Assert.That(payload.BlockAccessListHash, Is.EqualTo(balHash));

        Result<Block> decoded = payload.TryGetBlock();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.IsError, Is.False);
            Assert.That(decoded.Data!.Header.BlockAccessListHash, Is.EqualTo(balHash));
            Assert.That(decoded.Data.BlockAccessList, Is.Null, "the sidecar is paired later by NewPayloadHandler");
        }
    }
}
