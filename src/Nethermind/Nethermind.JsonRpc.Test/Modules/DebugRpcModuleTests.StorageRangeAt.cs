// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.State;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    private const int StorageRangeInitialSlots = 5;

    [Test]
    public async Task Debug_storageRangeAt_pages_slots_in_hashed_key_order([Values] bool flatDb, [Values(1, 2, 10)] int maxResult)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithFlatDb(flatDb).Build();
        (Address contract, Block block) = await AddStorageRangeBlocks(chain);

        List<(string Key, string Value)> slots = [];
        string? keyStart = "0x";
        int pages = 0;
        while (keyStart is not null)
        {
            JToken result = await StorageRangeAt(chain, block.Hash!, 0, contract, keyStart, maxResult);
            JObject storage = (JObject)result["storage"]!;
            Assert.That(storage.Count, Is.LessThanOrEqualTo(maxResult));
            foreach (JProperty slot in storage.Properties())
            {
                Assert.That(slot.Value["key"]!.Type, Is.EqualTo(JTokenType.Null), "slot key preimages are not kept");
                slots.Add((slot.Name, (string)slot.Value["value"]!));
            }

            keyStart = (string?)result["nextKey"];
            pages++;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slots, Is.EqualTo(ExpectedSlots(StorageRangeInitialSlots, extraSlots: 0)));
            Assert.That(pages, Is.EqualTo((StorageRangeInitialSlots + maxResult - 1) / maxResult));
        }
    }

    [Test]
    public async Task Debug_storageRangeAt_returns_storage_before_the_transaction([Values] bool flatDb, [Values(0, 1)] int txIndex)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithFlatDb(flatDb).Build();
        (Address contract, Block block) = await AddStorageRangeBlocks(chain);

        JToken result = await StorageRangeAt(chain, block.Hash!, txIndex, contract, "0x", 100);

        List<(string, string)> slots = ((JObject)result["storage"]!).Properties().Select(static p => (p.Name, (string)p.Value["value"]!)).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(slots, Is.EqualTo(ExpectedSlots(StorageRangeInitialSlots, extraSlots: txIndex)));
            Assert.That(result["nextKey"]!.Type, Is.EqualTo(JTokenType.Null));
        }
    }

    [Test]
    public async Task Debug_storageRangeAt_returns_null_storage_for_account_without_storage([Values] bool flatDb, [Values] bool accountExists)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithFlatDb(flatDb).Build();
        (_, Block block) = await AddStorageRangeBlocks(chain);
        Address address = accountExists ? TestItem.AddressB : TestItem.GetRandomAddress();

        JToken result = await StorageRangeAt(chain, block.Hash!, 1, address, "0x", 100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result["storage"]!.Type, Is.EqualTo(JTokenType.Null));
            Assert.That(result["nextKey"]!.Type, Is.EqualTo(JTokenType.Null));
        }
    }

    [Test]
    public async Task Debug_storageRangeAt_rejects_genesis_and_out_of_range_index([Values] bool genesis)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        (Address contract, Block block) = await AddStorageRangeBlocks(chain);
        Hash256 blockHash = genesis ? chain.BlockTree.Genesis!.Hash! : block.Hash!;

        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_storageRangeAt", blockHash, genesis ? 0 : block.Transactions.Length, contract, "0x", 1);

        Assert.That((int?)JToken.Parse(response)["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidInput), response);
    }

    /// <summary>
    /// Deploys a contract with <see cref="StorageRangeInitialSlots"/> slots, then adds a block whose transaction
    /// <c>i</c> adds slot <c>StorageRangeInitialSlots + 1 + i</c>.
    /// </summary>
    private static async Task<(Address Contract, Block Block)> AddStorageRangeBlocks(TestRpcBlockchain chain)
    {
        // Each call stores 1 at the slot given by the call value.
        byte[] runtime = Prepare.EvmCode.PushData(1).Op(Instruction.CALLVALUE).Op(Instruction.SSTORE).STOP().Done;
        Prepare init = Prepare.EvmCode;
        for (int slot = 1; slot <= StorageRangeInitialSlots; slot++)
        {
            init.PushData(slot + 0x10).PushData(slot).Op(Instruction.SSTORE);
        }

        ulong nonce = chain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Address contract = ContractAddress.From(TestItem.AddressA, nonce);
        await chain.AddBlock(Build.A.Transaction.WithCode(init.ForInitOf(runtime).Done).WithNonce(nonce)
            .WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyA).TestObject);

        Transaction[] calls = new Transaction[2];
        for (int i = 0; i < calls.Length; i++)
        {
            calls[i] = Build.A.Transaction.WithTo(contract).WithValue((UInt256)(StorageRangeInitialSlots + 1 + i)).WithNonce(nonce + 1 + (ulong)i)
                .WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        }

        Block block = await chain.AddBlock(calls);
        Assert.That(block.Transactions, Has.Length.EqualTo(calls.Length), "precondition: every call is in the block");
        Assert.That(chain.WorldStateManager.GlobalStateReader.CollectStorageRange(block.Header, contract, default, 100).Slots,
            Has.Count.EqualTo(StorageRangeInitialSlots + calls.Length), "precondition: every call stored a slot");
        return (contract, block);
    }

    private static List<(string Key, string Value)> ExpectedSlots(int initialSlots, int extraSlots)
    {
        List<(string Key, string Value)> slots = [];
        for (int slot = 1; slot <= initialSlots + extraSlots; slot++)
        {
            UInt256 value = slot <= initialSlots ? (UInt256)(slot + 0x10) : UInt256.One;
            slots.Add((ValueKeccak.Compute(((UInt256)slot).ToBigEndian()).ToString(), new ValueHash256(value.ToBigEndian()).ToString()));
        }

        return slots.OrderBy(static s => s.Key, System.StringComparer.Ordinal).ToList();
    }

    private static async Task<JToken> StorageRangeAt(TestRpcBlockchain chain, Hash256 blockHash, int txIndex, Address address, string keyStart, int maxResult)
    {
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_storageRangeAt", blockHash, txIndex, address, keyStart, maxResult);
        JToken json = JToken.Parse(response);
        Assert.That(json["error"], Is.Null, response);
        return json["result"]!;
    }
}
