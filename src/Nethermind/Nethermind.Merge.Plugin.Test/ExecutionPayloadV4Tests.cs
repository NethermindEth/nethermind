// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
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

    [Test]
    public void Recursive_stark_round_trips_through_payload_and_block()
    {
        byte[] proof = [1, 2, 3];
        Hash256 depsHash = Keccak.Compute("deps");
        ExecutionPayloadV4 payload = new()
        {
            BlockNumber = 1,
            GasLimit = 30_000_000,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            StateRoot = Keccak.EmptyTreeHash,
            BlobGasUsed = 0,
            ExcessBlobGas = 0,
            SlotNumber = 0,
            RecursiveStarkProof = proof,
            RecursiveStarkBlockDepsHash = depsHash.Bytes.ToArray(),
        };

        Block block = payload.TryGetBlock().Data!;
        Assert.That(block.Header.RecursiveStark, Is.Not.Null);
        Assert.That(block.Header.RecursiveStark!.StarkProof, Is.EqualTo(proof));
        Assert.That(block.Header.RecursiveStark.BlockDepsHash, Is.EqualTo(depsHash));

        // Re-create a payload from the reconstructed block and round-trip again: recursive_stark
        // survives the payload boundary, so the block hash reconciles (it would not if the field
        // were dropped, since it is part of the header hash).
        Block reRoundTripped = ExecutionPayloadV4.Create(block).TryGetBlock().Data!;
        Assert.That(reRoundTripped.Header.CalculateHash(), Is.EqualTo(block.Header.CalculateHash()));
    }

    [Test]
    public void Recursive_stark_survives_json_round_trip()
    {
        byte[] proof = [1, 2, 3];
        Hash256 depsHash = Keccak.Compute("deps");
        Block block = Build.A.Block.WithNumber(1).TestObject;
        block.Header.BlobGasUsed = 0;
        block.Header.ExcessBlobGas = 0;
        block.Header.SlotNumber = 0;
        block.Header.RecursiveStark = new RecursiveStark(proof, depsHash);
        block.Header.Hash = block.Header.CalculateHash();
        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(block);

        EthereumJsonSerializer serializer = new();
        ExecutionPayloadV4 deserialized = serializer.Deserialize<ExecutionPayloadV4>(serializer.Serialize(payload));

        Assert.That(deserialized.RecursiveStarkProof, Is.EqualTo(proof));
        Assert.That(deserialized.RecursiveStarkBlockDepsHash, Is.EqualTo(depsHash.Bytes.ToArray()));
    }

    [Test]
    public void Recursive_stark_absent_when_block_has_none()
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;

        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(block);

        Assert.That(payload.RecursiveStarkProof, Is.Null);
        Assert.That(payload.TryGetBlock().Data!.Header.RecursiveStark, Is.Null);
    }

    private static IEnumerable<TestCaseData> MalformedBlockAccessLists()
    {
        yield return new TestCaseData(Array.Empty<byte>())
            .SetName("Empty_bytes");
        yield return new TestCaseData(new byte[] { 0xc1, 0xc0 })
            .SetName("Wrapped_empty_list");
    }
}
