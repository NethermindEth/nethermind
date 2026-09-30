// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
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
using Nethermind.Specs.Test;
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

    private sealed class NonPrefixBlockHashModule : Module, IBlockValidationModule;
}
