// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Mcp.Plugin.Tools;
using Nethermind.Mcp.Plugin.Tools.Abi;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>
/// ENS resolution through the Universal Resolver on a Sepolia-like chain, where the ENSv1 registry is legacy and only the
/// Universal Resolver (ENSv2) knows every name. Genesis holds a mock Universal Resolver at the canonical address that answers
/// fixed calls, and a legacy registry entry the tools must not prefer over it.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class McpEnsUniversalResolverTests
{
    // https://docs.ens.domains/learn/deployments/: the Universal Resolver (proxy) address on Mainnet, Sepolia and Holesky.
    private static readonly Address UniversalResolver = new("0xeEeEEEeE14D718C2B47D9923Deab1335E144EeEe");
    private static readonly Address LegacyRegistry = new("0x00000000000C2E074eC69A0dFb2997BA6C7d2e1e");
    private static readonly Address V2Resolver = new("0x00000000000000000000000000000000000e5503");
    private static readonly Address LegacyResolver = new("0x00000000000000000000000000000000000e5504");
    private static readonly Address V2Target = TestItem.AddressD;
    private static readonly Address MismatchedAccount = TestItem.AddressF;
    private static readonly Address LegacyTarget = TestItem.AddressE;

    private McpTestNode _node = null!;
    private McpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _node = await McpTestNode.Create(configureContainer: static builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Berlin.Instance) { ChainId = BlockchainIds.Sepolia })
            .AddScoped<IGenesisPostProcessor, UniversalResolverGenesis>());
        _client = await _node.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_node is not null) await _node.DisposeAsync();
    }

    [Test]
    public async Task Name_registered_only_in_ensv2_resolves_through_the_universal_resolver()
    {
        JsonElement result = await Success("resolve_ens", ("name", "V2Only.eth"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("name").GetString(), Is.EqualTo("v2only.eth"));
            Assert.That(result.GetProperty("node").GetString(), Is.EqualTo(McpEns.NameHash("v2only.eth").ToString()));
            Assert.That(result.GetProperty("address").GetString(), Is.EqualTo(V2Target.ToString(true, true)));
            Assert.That(result.GetProperty("resolver").GetString(), Is.EqualTo(V2Resolver.ToString(true, true)));
            Assert.That(result.GetProperty("resolvedVia").GetString(), Is.EqualTo("universal resolver"));
            Assert.That(result.GetProperty("universalResolver").GetString(), Is.EqualTo(UniversalResolver.ToString(true, true)));
            Assert.That(result.GetProperty("owner").ValueKind, Is.EqualTo(JsonValueKind.Null), "the legacy Sepolia registry's owner is not authoritative");
        }
    }

    [Test]
    public async Task Universal_resolver_answer_wins_over_the_legacy_sepolia_registry()
    {
        JsonElement result = await Success("resolve_ens", ("name", "legacy.eth"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("address").ValueKind, Is.EqualTo(JsonValueKind.Null), "a legacy-only registry record must not be served on Sepolia");
            Assert.That(result.GetProperty("resolver").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(result.GetProperty("resolvedVia").GetString(), Is.EqualTo("none"));
            Assert.That(result.GetProperty("message").GetString(), Does.Contain("no resolver"));
        }
    }

    [Test]
    public async Task Offchain_name_reports_ccip_read_as_unsupported()
    {
        JsonElement result = await Success("resolve_ens", ("name", "offchain.eth"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("address").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(result.GetProperty("offchain").GetBoolean(), Is.True);
            Assert.That(result.GetProperty("message").GetString(), Does.Contain("CCIP-Read").And.Contain("not supported"));
        }
    }

    [Test]
    public async Task Resolver_without_address_records_is_reported()
    {
        JsonElement result = await Success("resolve_ens", ("name", "noaddr.eth"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("address").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(result.GetProperty("message").GetString(), Does.Contain("does not support address records"));
        }
    }

    [Test]
    public async Task Lookup_address_uses_universal_resolver_reverse_resolution()
    {
        JsonElement verified = (await Success("lookup_address", ("address", V2Target.ToString()))).GetProperty("ens");
        JsonElement mismatched = (await Success("lookup_address", ("address", MismatchedAccount.ToString()))).GetProperty("ens");
        JsonElement none = await Success("lookup_address", ("address", LegacyTarget.ToString()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verified.GetProperty("name").GetString(), Is.EqualTo("v2only.eth"));
            Assert.That(verified.GetProperty("verified").GetBoolean(), Is.True);
            Assert.That(mismatched.GetProperty("name").GetString(), Is.EqualTo("v2only.eth"));
            Assert.That(mismatched.GetProperty("verified").GetBoolean(), Is.False, "a primary name that resolves elsewhere is not verified");
            Assert.That(none.TryGetProperty("ens", out _), Is.False, "the legacy reverse record is not used when the Universal Resolver has none");
        }
    }

    [TestCase(BlockchainIds.Mainnet, true, true)]
    [TestCase(BlockchainIds.Sepolia, true, false)]
    [TestCase(BlockchainIds.Holesky, true, true)]
    [TestCase(BlockchainIds.Hoodi, false, false)]
    [TestCase(BlockchainIds.Gnosis, false, false)]
    public void Chain_profile_lists_the_universal_resolver_where_ens_publishes_it(ulong chainId, bool hasUniversalResolver, bool registryAuthoritative)
    {
        McpChainProfile profile = new(new ChainSpec(), new TestSpecProvider(Berlin.Instance) { ChainId = chainId });
        IReadOnlyList<McpWellKnownContract> ens = [.. profile.WellKnownContracts.Where(static c => c.Kind == "ens")];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(profile.EnsUniversalResolverAddress, Is.EqualTo(hasUniversalResolver ? UniversalResolver : null));
            Assert.That(profile.EnsRegistryIsAuthoritative, Is.EqualTo(registryAuthoritative));
            Assert.That(ens.Any(static c => c.Address == UniversalResolver), Is.EqualTo(hasUniversalResolver));
            if (chainId == BlockchainIds.Sepolia)
            {
                Assert.That(ens.Single(static c => c.Address == LegacyRegistry).Name, Does.Contain("legacy"));
            }
        }
    }

    private async Task<JsonElement> Success(string tool, params (string Name, object? Value)[] arguments)
    {
        CallToolResult result = await McpToolCalls.Call(_client, tool, arguments);
        JsonElement value = McpAssert.Success(result);
        await McpToolCalls.AssertConformsToOutputSchema(_client, tool, result);
        return value;
    }

    private static byte[] Encode(string signature, params object[] args)
    {
        JsonElement[] values = [.. args.Select(static a => JsonSerializer.SerializeToElement(a))];
        Assert.That(McpAbiCodec.TryEncodeCall(McpAbiSignature.Parse(signature), values, 64 * 1024, out byte[]? data, out string? error), Is.True, error);
        return data!;
    }

    private static byte[] EncodeTuple(string types, params object[] args) => Encode($"f({types})", args)[4..];

    private static string Hex(byte[] bytes) => "0x" + Convert.ToHexStringLower(bytes);

    private static byte[] DnsEncode(string name)
    {
        List<byte> result = [];
        foreach (string label in name.Split('.'))
        {
            result.Add((byte)label.Length);
            result.AddRange(Encoding.ASCII.GetBytes(label));
        }

        result.Add(0);
        return [.. result];
    }

    private static byte[] ResolveAddrCall(string name) =>
        Encode("resolve(bytes,bytes)", Hex(DnsEncode(name)), Hex(Encode("addr(bytes32)", McpEns.NameHash(name).ToString())));

    private static byte[] ReverseCall(Address account) => Encode("reverse(bytes,uint256)", Hex(account.Bytes.ToArray()), 60);

    private static string Word(Address address) => "0x" + new string('0', 24) + address.ToString(false, false);

    /// <summary>Genesis with a mock Universal Resolver answering fixed calls and a legacy ENSv1 registry holding a name the Universal Resolver does not know.</summary>
    private sealed class UniversalResolverGenesis(IWorldState state, ISpecProvider specProvider) : IGenesisPostProcessor
    {
        public void PostProcess(Block genesis)
        {
            IReleaseSpec spec = specProvider.GenesisSpec;
            (byte[] Call, byte[] Blob, bool Revert)[] answers =
            [
                (ResolveAddrCall("v2only.eth"), EncodeTuple("bytes,address", Word(V2Target), V2Resolver.ToString()), false),
                (ResolveAddrCall("offchain.eth"), Encode("OffchainLookup(address,string[],bytes,bytes4,bytes)",
                    UniversalResolver.ToString(), new[] { "https://gateway.example/{sender}/{data}.json" }, "0x1234", "0x12345678", "0x"), true),
                (ResolveAddrCall("noaddr.eth"), Encode("UnsupportedResolverProfile(bytes4)", "0x3b3b57de"), true),
                (ReverseCall(V2Target), EncodeTuple("string,address,address", "v2only.eth", V2Resolver.ToString(), V2Resolver.ToString()), false),
                (ReverseCall(MismatchedAccount), Encode("ReverseAddressMismatch(string,bytes)", "v2only.eth", Hex(V2Target.Bytes.ToArray())), true),
            ];

            state.CreateAccount(UniversalResolver, UInt256.Zero);
            state.InsertCode(UniversalResolver, TestContracts.CalldataResponder([.. answers.Select(static a => (a.Blob, a.Revert))],
                Encode("ResolverNotFound(bytes)", "0x")), spec, isGenesis: true);
            for (int i = 0; i < answers.Length; i++)
            {
                state.Set(new StorageCell(UniversalResolver, new UInt256(Keccak.Compute(answers[i].Call).Bytes, isBigEndian: true)), (UInt256)(i + 1));
            }

            // The legacy registry still knows legacy.eth and LegacyTarget's reverse record; the tools must not use them while the Universal Resolver answers.
            Hash256 legacy = McpEns.NameHash("legacy.eth");
            state.CreateAccount(LegacyRegistry, UInt256.Zero);
            state.InsertCode(LegacyRegistry, TestContracts.Registry(), spec, isGenesis: true);
            state.Set(new StorageCell(LegacyRegistry, Slot(legacy)), Value(TestItem.AddressA));
            state.Set(new StorageCell(LegacyRegistry, Slot(legacy) + UInt256.One), Value(LegacyResolver));
            state.Set(new StorageCell(LegacyRegistry, Slot(McpEns.NameHash(McpEns.ReverseName(LegacyTarget))) + UInt256.One), Value(LegacyResolver));
            state.CreateAccount(LegacyResolver, UInt256.Zero);
            state.InsertCode(LegacyResolver, TestContracts.Resolver("legacy.eth"), spec, isGenesis: true);
            state.Set(new StorageCell(LegacyResolver, Slot(legacy)), Value(LegacyTarget));
        }

        private static UInt256 Slot(Hash256 node) => new(node.Bytes, isBigEndian: true);

        private static UInt256 Value(Address address)
        {
            byte[] word = new byte[32];
            address.Bytes.CopyTo(word.AsSpan(12));
            return new UInt256(word, isBigEndian: true);
        }
    }
}
