// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp.Eip7928;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
public class ExecutionPayloadV4Tests
{
    [TestCaseSource(nameof(MalformedBlockAccessLists))]
    public void TryGetBlock_returns_decoding_error_for_malformed_block_access_list(byte[] blockAccessList)
    {
        ExecutionPayloadV4 payload = new()
        {
            BlockAccessList = blockAccessList,
            SlotNumber = 0,
            BlockNumber = 1,
            GasLimit = 30_000_000,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            StateRoot = Keccak.EmptyTreeHash,
        };

        Result<Block> result = payload.TryGetBlock();

        Assert.That(result.Data, Is.Null);
        Assert.That(result.Error, Does.StartWith("Error decoding block access list:"));
    }

    [Test]
    public void TryGetBlock_reuses_cached_wire_hash_for_block_access_list_hash()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(0, 1, 2).TestObject,
                Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageReads(0).TestObject)
            .TestObject;
        byte[] encoded = BlockAccessListDecoder.EncodeToBytes(bal);

        ExecutionPayloadV4 payload = new()
        {
            BlockAccessList = encoded,
            SlotNumber = 0,
            BlockNumber = 1,
            GasLimit = 30_000_000,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            StateRoot = Keccak.EmptyTreeHash,
        };

        Result<Block> result = payload.TryGetBlock();

        Assert.That(result.Data, Is.Not.Null);
        Block block = result.Data!;
        Hash256 expected = new(ValueKeccak.Compute(encoded).Bytes);
        Assert.That(block.Header.BlockAccessListHash, Is.EqualTo(expected));
        Assert.That(block.Header.BlockAccessListHash, Is.EqualTo(block.BlockAccessList!.WireHash));
    }

    private static IEnumerable<TestCaseData> MalformedBlockAccessLists()
    {
        yield return new TestCaseData(Array.Empty<byte>())
            .SetName("Empty_bytes");
        yield return new TestCaseData(new byte[] { 0xc1, 0xc0 })
            .SetName("Wrapped_empty_list");
    }
}
