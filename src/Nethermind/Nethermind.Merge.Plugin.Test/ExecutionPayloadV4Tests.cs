// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp.Eip7928;
using Nethermind.Specs.ChainSpecStyle;
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data, Is.Null);
            Assert.That(result.Error, Does.StartWith("Error decoding block access list:"));
        }
        Assert.That(result.Error, Does.Not.Contain(Environment.NewLine));
    }

    [Test]
    public void TryGetBlock_commits_the_header_to_the_dependency_proof_and_Create_carries_it_back([Values] bool empty)
    {
        byte[] proof = empty ? [] : [Eip8141Constants.DependencyProofVersionRaw, 0xc1, 0x01];
        ExecutionPayloadV4 payload = new()
        {
            BlockAccessList = BlockAccessListDecoder.EncodeToBytes(Build.A.BlockAccessList.TestObject),
            SlotNumber = 0,
            BlockNumber = 1,
            GasLimit = 30_000_000,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            StateRoot = Keccak.EmptyTreeHash,
            DependencyProof = proof,
        };

        Block block = payload.TryGetBlock().Data!;
        ExecutionPayloadV4 roundTripped = ExecutionPayloadV4.Create(block);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Header.DependencyProofHash, Is.EqualTo(Keccak.Compute(proof)));
            Assert.That(block.DependencyProof, Is.SameAs(proof));
            Assert.That(roundTripped.DependencyProof, Is.EqualTo(proof));
        }
    }

    [Test]
    public void TryGetBlock_refuses_a_dependency_proof_past_its_maximum()
    {
        ExecutionPayloadV4 payload = new()
        {
            BlockAccessList = BlockAccessListDecoder.EncodeToBytes(Build.A.BlockAccessList.TestObject),
            SlotNumber = 0,
            BlockNumber = 1,
            GasLimit = 30_000_000,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            StateRoot = Keccak.EmptyTreeHash,
            DependencyProof = new byte[Eip8141Constants.MaxDependencyProofBytes + 1],
        };

        Assert.That(payload.TryGetBlock().Error, Does.Contain("MAX_DEPENDENCY_PROOF_BYTES"));
    }

    [TestCase(5UL, false)]
    [TestCase(6UL, true)]
    public void TryGetBlock_checks_the_block_access_list_item_limit_before_decoding(ulong itemLimit, bool decoded)
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(0, 1, 2).TestObject,
                Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageReads(0).TestObject)
            .TestObject;
        ExecutionPayloadV4 payload = new()
        {
            BlockAccessList = BlockAccessListDecoder.EncodeToBytes(bal),
            SlotNumber = 0,
            BlockNumber = 1,
            GasLimit = itemLimit * Eip7928Constants.ItemCost,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            StateRoot = Keccak.EmptyTreeHash,
        };

        Result<Block> result = payload.TryGetBlock();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsError, Is.EqualTo(!decoded));
            if (!decoded) Assert.That(result.Error, Does.StartWith("BlockAccessListGasLimitExceeded"));
        }
    }

    [Test]
    public void TryGetBlock_keeps_a_deferred_block_access_list_encoded()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(0, 1, 2).TestObject)
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

        bool deferred = payload.TryDeferBlockAccessListDecode(out string? error);
        Block block = payload.TryGetBlock().Data!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deferred, Is.True, error);
            Assert.That(block.BlockAccessList, Is.Null);
            Assert.That(block.EncodedBlockAccessList, Is.SameAs(encoded));
            Assert.That(block.Header.BlockAccessListHash, Is.EqualTo(Keccak.Compute(encoded)));
        }
    }

    [Test]
    public void TryDeferBlockAccessListDecode_checks_the_item_limit()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(0, 1, 2).TestObject)
            .TestObject;
        ExecutionPayloadV4 payload = new()
        {
            BlockAccessList = BlockAccessListDecoder.EncodeToBytes(bal),
            GasLimit = 3 * Eip7928Constants.ItemCost,
        };

        bool deferred = payload.TryDeferBlockAccessListDecode(out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deferred, Is.False);
            Assert.That(error, Does.StartWith("BlockAccessListGasLimitExceeded"));
        }
    }

    [Test]
    public void TryGetBlock_checks_the_item_limit_of_a_cached_block_access_list()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(0, 1, 2).TestObject,
                Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageReads(0).TestObject)
            .TestObject;
        ExecutionPayloadV4 payload = new()
        {
            BlockAccessList = BlockAccessListDecoder.EncodeToBytes(bal),
            SlotNumber = 0,
            BlockNumber = 1,
            GasLimit = 6 * Eip7928Constants.ItemCost,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            StateRoot = Keccak.EmptyTreeHash,
        };

        Result<Block> withinLimit = payload.TryGetBlock();
        payload.GasLimit = 5 * Eip7928Constants.ItemCost;
        Result<Block> overLimit = payload.TryGetBlock();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withinLimit.IsError, Is.False);
            Assert.That(overLimit.Error, Does.StartWith("BlockAccessListGasLimitExceeded"));
        }
    }

    [Test]
    public void TryGetBlock_reuses_cached_block_access_list_for_wire_hash()
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
        Result<Block> cachedResult = payload.TryGetBlock();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data, Is.Not.Null);
            Assert.That(cachedResult.Data, Is.Not.Null);
        }
        Block block = result.Data!;
        Assert.That(cachedResult.Data!.BlockAccessList, Is.SameAs(block.BlockAccessList));
        Hash256 expected = new(ValueKeccak.Compute(encoded).Bytes);
        Assert.That(block.Header.BlockAccessListHash, Is.EqualTo(expected));
        Assert.That(block.Header.BlockAccessListHash, Is.EqualTo(block.BlockAccessList!.WireHash));
    }

    // Two devnet fixture lines both call their Amsterdam successor Bogota while meaning different
    // things by it, so the label a genesis carries has to decide which newPayload version the node
    // accepts: inclusion lists move it to V6, frame transactions leave it on Amsterdam's V5.
    [TestCase("bogotaTime", EngineApiVersions.NewPayload.V6)]
    [TestCase("eip8141PrototypeTime", EngineApiVersions.NewPayload.V5)]
    public void ValidateForkOnNewPayload_accepts_the_version_the_genesis_fork_label_selects(string label, int accepted)
    {
        string genesis = $$"""
            {
              "config": {
                "chainId": 1, "homesteadBlock": 0, "eip150Block": 0, "eip155Block": 0, "eip158Block": 0, "byzantiumBlock": 0,
                "constantinopleBlock": 0, "petersburgBlock": 0, "istanbulBlock": 0, "berlinBlock": 0, "londonBlock": 0,
                "shanghaiTime": 0, "amsterdamTime": 15, "{{label}}": 15
              },
              "difficulty": "0x1",
              "gasLimit": "0x8000000",
              "alloc": {}
            }
            """;
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(genesis));
        ChainSpec chainSpec = new GethGenesisLoader(new EthereumJsonSerializer()).Load(stream);
        ChainSpecBasedSpecProvider specProvider = new(chainSpec);

        ExecutionPayloadV4 payload = new()
        {
            BlockAccessList = [],
            SlotNumber = 0,
            BlockNumber = 1,
            Timestamp = 15,
            GasLimit = 30_000_000,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            StateRoot = Keccak.EmptyTreeHash,
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.ValidateForkOnNewPayload(specProvider, EngineApiVersions.NewPayload.V5),
                Is.EqualTo(accepted == EngineApiVersions.NewPayload.V5));
            Assert.That(payload.ValidateForkOnNewPayload(specProvider, EngineApiVersions.NewPayload.V6),
                Is.EqualTo(accepted == EngineApiVersions.NewPayload.V6));
        }
    }

    [TestCase(typeof(ExecutionPayloadV4), null, null)]
    [TestCase(typeof(ExecutionPayloadV4), "withdrawals", "withdrawals")]
    [TestCase(typeof(ExecutionPayloadV4), "blobGasUsed", "blobGasUsed")]
    [TestCase(typeof(ExecutionPayloadV4), "blockAccessList", "blockAccessList")]
    [TestCase(typeof(ExecutionPayloadV4), "slotNumber", "slotNumber")]
    [TestCase(typeof(ExecutionPayloadV3), "excessBlobGas", "excessBlobGas")]
    [TestCase(typeof(ExecutionPayloadV3), "blockAccessList", null)]
    [TestCase(typeof(ExecutionPayloadV3), "slotNumber", null)]
    public void Deserialization_tracks_the_required_fields_of_the_payload_version(Type payloadType, string? omittedField, string? expectedUnboundField)
    {
        ExecutionPayloadV4 complete = new() { Withdrawals = [], BlobGasUsed = 0, ExcessBlobGas = 0, BlockAccessList = [0xc0], SlotNumber = 1 };
        JsonObject json = JsonSerializer.SerializeToNode(complete, EthereumJsonSerializer.JsonOptions)!.AsObject();
        if (omittedField is not null) json.Remove(omittedField);

        ExecutionPayload payload = (ExecutionPayload)json.Deserialize(payloadType, EthereumJsonSerializer.JsonOptions)!;

        Assert.That(payload.HasUnboundField ? payload.UnboundFieldName : null, Is.EqualTo(expectedUnboundField));
    }

    private static IEnumerable<TestCaseData> MalformedBlockAccessLists()
    {
        yield return new TestCaseData(Array.Empty<byte>())
            .SetName("Empty_bytes");
        yield return new TestCaseData(new byte[] { 0xc1, 0xc0 })
            .SetName("Wrapped_empty_list");
    }
}
