// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm.State;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    // CALLDATALOAD(4); slot = keccak(pad32(arg) ++ pad32(0)); return pad32(SLOAD(slot))
    private const string BalanceOfBody = "600435600052600060205260406000205460005260206000f3";

    // Reads (and ignores) fixed slot 1 before the balanceOf body, so the derived template carries one value-guard.
    // Calls with calldata other than 36 bytes take the write path: SSTORE(1, SLOAD(1) + 1).
    private const string GuardedBalanceOfCode = "36602414601157600154600101600155005b60015450" + BalanceOfBody;

    // return pad32(CALLDATALOAD(4) * 2) — no storage reads, so derivation must blacklist.
    private const string DoublerCode = "60043560020260005260206000f3";

    private const uint BalanceOfSelector = 0x70a08231;
    private static readonly Address TemplateContract = new("0xc0de000000000000000000000000000000000001");
    private static readonly UInt256 GuardSlot = 1;

    private static readonly UInt256 ArgA = new(0xAAAA);
    private static readonly UInt256 ArgB = new(0xBBBB);
    private static readonly UInt256 BalanceA = 100;
    private static readonly UInt256 BalanceB = 200;

    private static UInt256 MappingSlot(in UInt256 arg, byte slotIndex)
    {
        byte[] material = new byte[64];
        arg.ToBigEndian(material.AsSpan(0, 32));
        material[63] = slotIndex;
        return new UInt256(ValueKeccak.Compute(material).Bytes, isBigEndian: true);
    }

    private static async Task<TestRpcBlockchain> CreateTemplatesChain(string codeHex, bool enabled = true, bool shadowMode = true) =>
        await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EthCallTemplates = enabled, EthCallTemplatesShadowMode = shadowMode })
            .Build(builder => builder.WithGenesisPostProcessor((_, state, specProvider) =>
            {
                state.CreateAccount(TemplateContract, 0);
                state.InsertCode(TemplateContract, Bytes.FromHexString(codeHex), specProvider.GenesisSpec);
                state.Set(new StorageCell(TemplateContract, MappingSlot(ArgA, 0)), BalanceA.ToBigEndian());
                state.Set(new StorageCell(TemplateContract, MappingSlot(ArgB, 0)), BalanceB.ToBigEndian());
                state.Set(new StorageCell(TemplateContract, GuardSlot), ((UInt256)0xAA).ToBigEndian());
            }));

    private static Task<string> CallTemplateContract(TestRpcBlockchain rpc, in UInt256 arg)
    {
        byte[] input = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(input, BalanceOfSelector);
        arg.ToBigEndian(input.AsSpan(4));
        TransactionForRpc call = rpc.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"to\": \"{TemplateContract}\", \"input\": \"{input.ToHexString(true)}\"}}");
        return rpc.TestEthRpc("eth_call", call, "latest");
    }

    private static string SuccessResponse(in UInt256 value) =>
        $"{{\"jsonrpc\":\"2.0\",\"result\":\"{value.ToBigEndian().ToHexString(true)}\",\"id\":67}}";

    [Test]
    public async Task Eth_call_templates_two_executions_with_different_args_derive_template()
    {
        using TestRpcBlockchain rpc = await CreateTemplatesChain(BalanceOfBody);

        Assert.That(await CallTemplateContract(rpc, ArgA), Is.EqualTo(SuccessResponse(BalanceA)));
        Assert.That(await CallTemplateContract(rpc, ArgB), Is.EqualTo(SuccessResponse(BalanceB)));

        Assert.That(rpc.EthCallTemplates!.TemplatesDerived, Is.EqualTo(1));
        Assert.That(rpc.EthCallTemplates.TemplatesBlacklisted, Is.EqualTo(0));
    }

    [Test]
    public async Task Eth_call_templates_shadow_mode_serves_evm_result_and_records_match()
    {
        using TestRpcBlockchain rpc = await CreateTemplatesChain(BalanceOfBody);

        await CallTemplateContract(rpc, ArgA);
        await CallTemplateContract(rpc, ArgB);
        Assert.That(await CallTemplateContract(rpc, ArgA), Is.EqualTo(SuccessResponse(BalanceA)));

        Assert.That(rpc.EthCallTemplates!.TemplateShadowMatches, Is.EqualTo(1));
        Assert.That(rpc.EthCallTemplates.TemplateShadowMismatches, Is.EqualTo(0));
        Assert.That(rpc.EthCallTemplates.TemplateHits, Is.EqualTo(0));
    }

    [Test]
    public async Task Eth_call_templates_non_shadow_mode_serves_template_answer()
    {
        using TestRpcBlockchain rpc = await CreateTemplatesChain(BalanceOfBody, shadowMode: false);

        await CallTemplateContract(rpc, ArgA);
        await CallTemplateContract(rpc, ArgB);
        Assert.That(await CallTemplateContract(rpc, ArgA), Is.EqualTo(SuccessResponse(BalanceA)));
        Assert.That(await CallTemplateContract(rpc, ArgB), Is.EqualTo(SuccessResponse(BalanceB)));

        Assert.That(rpc.EthCallTemplates!.TemplateHits, Is.EqualTo(2));
        Assert.That(rpc.EthCallTemplates.TemplateShadowMatches, Is.EqualTo(0));
    }

    [Test]
    public async Task Eth_call_templates_changed_guard_slot_invalidates_template()
    {
        using TestRpcBlockchain rpc = await CreateTemplatesChain(GuardedBalanceOfCode, shadowMode: false);

        await CallTemplateContract(rpc, ArgA);
        await CallTemplateContract(rpc, ArgB);
        Assert.That(rpc.EthCallTemplates!.TemplatesDerived, Is.EqualTo(1));

        // An empty-calldata transaction takes the contract's write path, bumping the guarded slot.
        Transaction bumpGuard = Build.A.Transaction
            .WithTo(TemplateContract)
            .WithGasLimit(100_000)
            .WithNonce(rpc.ReadOnlyState.GetNonce(TestItem.AddressA))
            .SignedAndResolved(rpc.EthereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;
        await rpc.AddBlock(bumpGuard);

        Assert.That(await CallTemplateContract(rpc, ArgA), Is.EqualTo(SuccessResponse(BalanceA)));
        Assert.That(rpc.EthCallTemplates.GuardInvalidations, Is.EqualTo(1));
        Assert.That(rpc.EthCallTemplates.TemplateHits, Is.EqualTo(0));

        // Relearning from the post-change state derives a fresh template.
        Assert.That(await CallTemplateContract(rpc, ArgB), Is.EqualTo(SuccessResponse(BalanceB)));
        Assert.That(rpc.EthCallTemplates.TemplatesDerived, Is.EqualTo(2));
    }

    [Test]
    public async Task Eth_call_templates_computed_output_contract_is_blacklisted()
    {
        using TestRpcBlockchain rpc = await CreateTemplatesChain(DoublerCode);

        Assert.That(await CallTemplateContract(rpc, ArgA), Is.EqualTo(SuccessResponse(ArgA * 2)));
        Assert.That(await CallTemplateContract(rpc, ArgB), Is.EqualTo(SuccessResponse(ArgB * 2)));

        Assert.That(rpc.EthCallTemplates!.TemplatesBlacklisted, Is.EqualTo(1));
        Assert.That(rpc.EthCallTemplates.TemplatesDerived, Is.EqualTo(0));

        Assert.That(await CallTemplateContract(rpc, ArgA), Is.EqualTo(SuccessResponse(ArgA * 2)));
        Assert.That(rpc.EthCallTemplates.TemplatesBlacklisted, Is.EqualTo(1));
    }

    [Test]
    public async Task Eth_call_templates_disabled_records_nothing()
    {
        using TestRpcBlockchain rpc = await CreateTemplatesChain(BalanceOfBody, enabled: false);

        Assert.That(await CallTemplateContract(rpc, ArgA), Is.EqualTo(SuccessResponse(BalanceA)));
        Assert.That(await CallTemplateContract(rpc, ArgB), Is.EqualTo(SuccessResponse(BalanceB)));

        Assert.That(rpc.EthCallTemplates, Is.Null);
    }
}
