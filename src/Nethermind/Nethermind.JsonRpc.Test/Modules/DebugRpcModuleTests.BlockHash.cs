// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using AddBlockResult = Nethermind.Blockchain.AddBlockResult;
using BlockTreeLookupOptions = Nethermind.Blockchain.BlockTreeLookupOptions;
using BlockTreeSuggestOptions = Nethermind.Blockchain.BlockTreeSuggestOptions;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    public async Task Debug_traceCall_blockhash_uses_selected_ancestry(
        [Values(-1, 0)] int index, [Values] bool blockAccessLists, [Values] bool prefixCompatible)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder =>
            {
                builder.AddSingleton<ISpecProvider>(new TestSpecProvider(blockAccessLists ? Amsterdam.Instance : Prague.Instance) { AllowTestChainOverride = false });
                if (!prefixCompatible) builder.AddSingleton<IBlockValidationModule>(new NonPrefixBlockHashModule());
            });
        IDebugRpcModule module = chain.DebugRpcModule;
        Block block = await AddTraceCallPrefixTransfers(chain, 2);
        BlockHeader header = block.Header;
        string original = Nethermind.Serialization.Rlp.Rlp.Encode(header).ToString();
        object call = new { from = TestItem.AddressA.ToString(), to = TestItem.AddressD.ToString(), gas = "0x186a0" };

        foreach (int? delta in new int?[] { null, -1, 0, 1, 2, 255, 256 })
        {
            ulong current = delta is null ? header.Number : (ulong)((long)header.Number + delta.Value);
            ulong reference = delta == 1 ? header.Number + 1 : header.Number;
            foreach (ulong number in new[] { header.Number - 1, header.Number, header.Number + 1 })
            {
                byte[] code = Prepare.EvmCode.PushData(number).Op(Instruction.BLOCKHASH)
                    .PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done;
                Dictionary<string, object> overrides = new()
                {
                    [TestItem.AddressD.ToString()] = new { code = code.ToHexString(true) }
                };
                string response = await RpcTest.TestSerializedRequest(module, "debug_traceCall", call, header.Hash!.ToString(),
                    new
                    {
                        txIndex = index < 0 ? null : "0x0",
                        stateOverrides = overrides,
                        blockOverrides = delta is null ? null : new { number = $"0x{current:x}" }
                    });
                JToken json = JToken.Parse(response);
                Assert.That(json["error"], Is.Null, response);
                Hash256 expected = number < current && current - number <= 256 && number < reference
                    ? chain.BlockTree.FindHeader(number)!.Hash!
                    : Keccak.Zero;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That((string?)json["result"]?["returnValue"], Is.EqualTo(expected.ToString()), $"delta={delta}, number={number}: {response}");
                    Assert.That(Nethermind.Serialization.Rlp.Rlp.Encode(header).ToString(), Is.EqualTo(original));
                }
            }
        }

        string failure = await RpcTest.TestSerializedRequest(module, "debug_traceCall", call, header.Hash!.ToString(),
            new
            {
                txIndex = index < 0 ? null : "0x0",
                blockOverrides = new { number = $"0x{header.Number + 1:x}" },
                tracer = "{fault:function(){},result:function(){throw Error('blockhash reset');}}"
            });
        Assert.That(JToken.Parse(failure)["error"], Is.Not.Null, failure);

        // A subsequent unmodified call on the same pooled module must not retain the failed call's reference header.
        byte[] parentCode = Prepare.EvmCode.PushData(header.Number - 1).Op(Instruction.BLOCKHASH)
            .PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done;
        string normal = await RpcTest.TestSerializedRequest(module, "debug_traceCall", call, header.Hash!.ToString(),
            new { stateOverrides = new Dictionary<string, object> { [TestItem.AddressD.ToString()] = new { code = parentCode.ToHexString(true) } } });
        Assert.That((string?)JToken.Parse(normal)["result"]?["returnValue"], Is.EqualTo(header.ParentHash!.ToString()), normal);
    }

    [Test]
    public async Task Debug_traceCall_hash_selector_ignores_requireCanonical(
        [Values] bool requireCanonical, [Values] bool indexed)
    {
        using Context context = await Context.Create();
        await AddBlockWithTransfer(context);
        Block canonical = context.Blockchain.BlockTree.Head!;
        BlockHeader parent = context.Blockchain.BlockTree.FindHeader(canonical.ParentHash!, BlockTreeLookupOptions.None)!;
        Address beneficiary = canonical.Beneficiary == TestItem.AddressD ? TestItem.AddressE : TestItem.AddressD;
        // Flat-state retention identifies snapshots by both height and root, not by root alone.
        Block sibling = Build.A.Block.WithParent(parent).WithStateRoot(canonical.StateRoot!)
            .WithBeneficiary(beneficiary).WithExtraData([1]).TestObject;
        AddBlockResult suggested = context.Blockchain.BlockTree.SuggestBlock(sibling, BlockTreeSuggestOptions.ForceDontSetAsMain);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(suggested, Is.EqualTo(AddBlockResult.Added));
            Assert.That(sibling.Hash, Is.Not.EqualTo(canonical.Hash));
            Assert.That(sibling.Transactions, Is.Empty);
            Assert.That(context.Blockchain.StateReader.HasStateForBlock(sibling.Header), Is.True,
                "The non-indexed call must have a retained snapshot at the sibling's own height and root.");
            Assert.That(context.Blockchain.BlockTree.FindBlock(canonical.Number, BlockTreeLookupOptions.RequireCanonical)!.Hash,
                Is.EqualTo(canonical.Hash));
        }

        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceCall",
            new { from = TestItem.AddressA.ToString(), to = TestItem.AddressC.ToString(), gas = "0x186a0" },
            new { blockHash = sibling.Hash!.ToString(), requireCanonical },
            new
            {
                txIndex = indexed ? "0x0" : null,
                stateOverrides = new Dictionary<string, object>
                {
                    [TestItem.AddressC.ToString()] = new { code = "0x4160005260206000f3" }
                }
            });
        JToken json = JToken.Parse(response);
        Assert.That(json["error"], Is.Null, response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((string?)json["result"]?["returnValue"], Is.EqualTo("0x" + new string('0', 24) + beneficiary.ToString()[2..]), response);
            Assert.That(context.Blockchain.BlockTree.FindBlock(canonical.Number, BlockTreeLookupOptions.RequireCanonical)!.Hash,
                Is.EqualTo(canonical.Hash));
            Assert.That(sibling.Header.StateRoot, Is.EqualTo(canonical.StateRoot));
        }
    }

    private sealed class NonPrefixBlockHashModule : Module, IBlockValidationModule;
}
