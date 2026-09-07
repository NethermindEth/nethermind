// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
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

        Assert.That(result.Data, Is.Null);
        Assert.That(result.Error, Does.StartWith("Error decoding block access list:"));
        Assert.That(result.Error, Does.Not.Contain(Environment.NewLine));
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

        Assert.That(result.Data, Is.Not.Null);
        Assert.That(cachedResult.Data, Is.Not.Null);
        Block block = result.Data!;
        Assert.That(cachedResult.Data!.BlockAccessList, Is.SameAs(block.BlockAccessList));
        Hash256 expected = new(ValueKeccak.Compute(encoded).Bytes);
        Assert.That(block.Header.BlockAccessListHash, Is.EqualTo(expected));
        Assert.That(block.Header.BlockAccessListHash, Is.EqualTo(block.BlockAccessList!.WireHash));
    }

    // On this deployment image bogotaTime is the frame-transaction devnet's label and selects frame
    // transactions, which leaves newPayload on Amsterdam's V5; inclusion lists would move it to V6.
    [TestCase("bogotaTime", true)]
    [TestCase("eip8141PrototypeTime", false)]
    public void ValidateForkOnNewPayload_accepts_V5_for_a_bogota_labelled_genesis(string label, bool selectsFrames)
    {
        string genesis = $$"""
            {
              "config": { "chainId": 1, "homesteadBlock": 0, "amsterdamTime": 15, "{{label}}": 15 },
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

        IReleaseSpec spec = specProvider.GetSpec(ForkActivation.TimestampOnly(15));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(spec.IsEip8141Enabled, Is.EqualTo(selectsFrames));
            Assert.That(spec.IsEip7805Enabled, Is.False);
            Assert.That(payload.ValidateForkOnNewPayload(specProvider, EngineApiVersions.NewPayload.V5), Is.True);
            Assert.That(payload.ValidateForkOnNewPayload(specProvider, EngineApiVersions.NewPayload.V6), Is.False);
        }
    }

    private static IEnumerable<TestCaseData> MalformedBlockAccessLists()
    {
        yield return new TestCaseData(Array.Empty<byte>())
            .SetName("Empty_bytes");
        yield return new TestCaseData(new byte[] { 0xc1, 0xc0 })
            .SetName("Wrapped_empty_list");
    }
}
