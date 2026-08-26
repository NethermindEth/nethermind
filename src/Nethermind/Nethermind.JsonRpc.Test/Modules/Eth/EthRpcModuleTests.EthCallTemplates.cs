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

    // Mirrors EthCallTemplates.MaxDerivationAttempts.
    private const int MaxDerivationAttempts = 16;

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

    private static async Task<TestRpcBlockchain> CreateTemplatesChain(string codeHex, bool enabled = true, bool shadowMode = true, (UInt256 Arg, UInt256 Balance)[]? balances = null) =>
        await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EthCallTemplates = enabled, EthCallTemplatesShadowMode = shadowMode })
            .Build(builder => builder.WithGenesisPostProcessor((_, state, specProvider) =>
            {
                state.CreateAccount(TemplateContract, 0);
                state.InsertCode(TemplateContract, Bytes.FromHexString(codeHex), specProvider.GenesisSpec);
                foreach ((UInt256 arg, UInt256 balance) in balances ?? [(ArgA, BalanceA), (ArgB, BalanceB)])
                {
                    state.Set(new StorageCell(TemplateContract, MappingSlot(arg, 0)), balance.ToBigEndian());
                }
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

    private readonly record struct TemplateMetricsSnapshot(long Derived, long Blacklisted, long Hits, long ShadowMatches, long ShadowMismatches, long GuardInvalidations)
    {
        public static TemplateMetricsSnapshot Capture() => new(
            Metrics.EthCallTemplatesDerived,
            Metrics.EthCallTemplatesBlacklisted,
            Metrics.EthCallTemplateHits,
            Metrics.EthCallTemplateShadowMatches,
            Metrics.EthCallTemplateShadowMismatches,
            Metrics.EthCallTemplateGuardInvalidations);
    }

    [Test]
    public async Task Eth_call_templates_two_executions_with_different_args_derive_template()
    {
        using TestRpcBlockchain rpc = await CreateTemplatesChain(BalanceOfBody);
        TemplateMetricsSnapshot before = TemplateMetricsSnapshot.Capture();

        Assert.That(await CallTemplateContract(rpc, ArgA), Is.EqualTo(SuccessResponse(BalanceA)));
        Assert.That(await CallTemplateContract(rpc, ArgB), Is.EqualTo(SuccessResponse(BalanceB)));

        Assert.That(Metrics.EthCallTemplatesDerived, Is.EqualTo(before.Derived + 1));
        Assert.That(Metrics.EthCallTemplatesBlacklisted, Is.EqualTo(before.Blacklisted));
    }

    [Test]
    public async Task Eth_call_templates_shadow_mode_serves_evm_result_and_records_match()
    {
        using TestRpcBlockchain rpc = await CreateTemplatesChain(BalanceOfBody);
        TemplateMetricsSnapshot before = TemplateMetricsSnapshot.Capture();

        await CallTemplateContract(rpc, ArgA);
        await CallTemplateContract(rpc, ArgB);
        Assert.That(await CallTemplateContract(rpc, ArgA), Is.EqualTo(SuccessResponse(BalanceA)));

        Assert.That(Metrics.EthCallTemplateShadowMatches, Is.EqualTo(before.ShadowMatches + 1));
        Assert.That(Metrics.EthCallTemplateShadowMismatches, Is.EqualTo(before.ShadowMismatches));
        Assert.That(Metrics.EthCallTemplateHits, Is.EqualTo(before.Hits));
    }

    [Test]
    public async Task Eth_call_templates_non_shadow_mode_serves_template_answer()
    {
        using TestRpcBlockchain rpc = await CreateTemplatesChain(BalanceOfBody, shadowMode: false);
        TemplateMetricsSnapshot before = TemplateMetricsSnapshot.Capture();

        await CallTemplateContract(rpc, ArgA);
        await CallTemplateContract(rpc, ArgB);
        Assert.That(await CallTemplateContract(rpc, ArgA), Is.EqualTo(SuccessResponse(BalanceA)));
        Assert.That(await CallTemplateContract(rpc, ArgB), Is.EqualTo(SuccessResponse(BalanceB)));

        Assert.That(Metrics.EthCallTemplateHits, Is.EqualTo(before.Hits + 2));
        Assert.That(Metrics.EthCallTemplateShadowMatches, Is.EqualTo(before.ShadowMatches));
    }

    [Test]
    public async Task Eth_call_templates_changed_guard_slot_invalidates_template()
    {
        using TestRpcBlockchain rpc = await CreateTemplatesChain(GuardedBalanceOfCode, shadowMode: false);
        TemplateMetricsSnapshot before = TemplateMetricsSnapshot.Capture();

        await CallTemplateContract(rpc, ArgA);
        await CallTemplateContract(rpc, ArgB);
        Assert.That(Metrics.EthCallTemplatesDerived, Is.EqualTo(before.Derived + 1));

        // An empty-calldata transaction takes the contract's write path, bumping the guarded slot.
        Transaction bumpGuard = Build.A.Transaction
            .WithTo(TemplateContract)
            .WithGasLimit(100_000)
            .WithNonce(rpc.ReadOnlyState.GetNonce(TestItem.AddressA))
            .SignedAndResolved(rpc.EthereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;
        await rpc.AddBlock(bumpGuard);

        Assert.That(await CallTemplateContract(rpc, ArgA), Is.EqualTo(SuccessResponse(BalanceA)));
        Assert.That(Metrics.EthCallTemplateGuardInvalidations, Is.EqualTo(before.GuardInvalidations + 1));
        Assert.That(Metrics.EthCallTemplateHits, Is.EqualTo(before.Hits));

        // Relearning from the post-change state derives a fresh template.
        Assert.That(await CallTemplateContract(rpc, ArgB), Is.EqualTo(SuccessResponse(BalanceB)));
        Assert.That(Metrics.EthCallTemplatesDerived, Is.EqualTo(before.Derived + 2));
    }

    [Test]
    public async Task Eth_call_templates_computed_output_contract_is_blacklisted()
    {
        using TestRpcBlockchain rpc = await CreateTemplatesChain(DoublerCode);
        TemplateMetricsSnapshot before = TemplateMetricsSnapshot.Capture();

        Assert.That(await CallTemplateContract(rpc, ArgA), Is.EqualTo(SuccessResponse(ArgA * 2)));
        Assert.That(await CallTemplateContract(rpc, ArgB), Is.EqualTo(SuccessResponse(ArgB * 2)));

        Assert.That(Metrics.EthCallTemplatesBlacklisted, Is.EqualTo(before.Blacklisted + 1));
        Assert.That(Metrics.EthCallTemplatesDerived, Is.EqualTo(before.Derived));

        Assert.That(await CallTemplateContract(rpc, ArgA), Is.EqualTo(SuccessResponse(ArgA * 2)));
        Assert.That(Metrics.EthCallTemplatesBlacklisted, Is.EqualTo(before.Blacklisted + 1));
    }

    [Test]
    public async Task Eth_call_templates_equal_outputs_defer_derivation_until_distinct_pair()
    {
        // Two equal balances (the common zero-balance case in the wild) are ambiguous evidence: no template
        // may be derived from them, but the pair must stay learnable — a later distinct-output pair derives.
        UInt256 sharedBalance = 42;
        UInt256 argC = new(0xCCCC);
        UInt256 balanceC = 77;
        using TestRpcBlockchain rpc = await CreateTemplatesChain(
            BalanceOfBody, balances: [(ArgA, sharedBalance), (ArgB, sharedBalance), (argC, balanceC)]);
        TemplateMetricsSnapshot before = TemplateMetricsSnapshot.Capture();

        Assert.That(await CallTemplateContract(rpc, ArgA), Is.EqualTo(SuccessResponse(sharedBalance)));
        Assert.That(await CallTemplateContract(rpc, ArgB), Is.EqualTo(SuccessResponse(sharedBalance)));

        Assert.That(Metrics.EthCallTemplatesDerived, Is.EqualTo(before.Derived));
        Assert.That(Metrics.EthCallTemplatesBlacklisted, Is.EqualTo(before.Blacklisted));

        Assert.That(await CallTemplateContract(rpc, argC), Is.EqualTo(SuccessResponse(balanceC)));

        Assert.That(Metrics.EthCallTemplatesDerived, Is.EqualTo(before.Derived + 1));
        Assert.That(Metrics.EthCallTemplatesBlacklisted, Is.EqualTo(before.Blacklisted));
    }

    [Test]
    public async Task Eth_call_templates_equal_output_retries_exhaust_into_blacklist()
    {
        // Every argument holds the same balance, so each derivation attempt fails on equal outputs;
        // after MaxDerivationAttempts such failures the pair must be blacklisted.
        UInt256 sharedBalance = 42;
        (UInt256 Arg, UInt256 Balance)[] balances = new (UInt256, UInt256)[MaxDerivationAttempts + 1];
        for (int i = 0; i < balances.Length; i++)
        {
            balances[i] = (new UInt256((ulong)(0x1000 + i)), sharedBalance);
        }

        using TestRpcBlockchain rpc = await CreateTemplatesChain(BalanceOfBody, balances: balances);
        TemplateMetricsSnapshot before = TemplateMetricsSnapshot.Capture();

        foreach ((UInt256 arg, _) in balances)
        {
            Assert.That(await CallTemplateContract(rpc, arg), Is.EqualTo(SuccessResponse(sharedBalance)));
        }

        Assert.That(Metrics.EthCallTemplatesDerived, Is.EqualTo(before.Derived));
        Assert.That(Metrics.EthCallTemplatesBlacklisted, Is.EqualTo(before.Blacklisted + 1));
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
