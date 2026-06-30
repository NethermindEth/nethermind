// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Text.Json;
using Nethermind.Avalanche.Blocks;
using Nethermind.Avalanche.Parity;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.Avalanche.Genesis;

/// <summary>
/// Reconstructs the Avalanche C-Chain genesis (block 0) from the genesis JSON AvalancheGo passes to the VM in
/// <c>Initialize</c> (the <c>cChainGenesis</c> document): the genesis allocations become the genesis world state
/// (encoded with Coreth's 5-field account RLP), and the genesis header/block hash are derived from it.
/// </summary>
/// <remarks>
/// The state root is built directly in a raw Patricia trie using <see cref="AvalancheStateAccountDecoder"/>, so it
/// matches Coreth byte-for-byte (verified against mainnet block 0: state root <c>0xd65eb1b8…29cc</c>, block hash
/// <c>0x31ced5b9…96b</c>). The genesis header is the 16-field shape with a zero <c>ExtDataHash</c>; a
/// <c>baseFeePerGas</c> in the genesis JSON (subnets that launch on a fee-bearing fork) is honored if present.
/// Genesis allocations carrying contract <c>storage</c> are not yet supported (the mainnet C-Chain genesis has
/// none) and raise <see cref="NotSupportedException"/>.
/// </remarks>
public sealed class AvalancheCChainGenesis
{
    private AvalancheCChainGenesis(long chainId, AvalancheBlockHeader header, Hash256 stateRoot, Hash256 hash)
    {
        ChainId = chainId;
        Header = header;
        StateRoot = stateRoot;
        Hash = hash;
    }

    /// <summary>The EVM chain id from <c>config.chainId</c> (43114 for the mainnet C-Chain).</summary>
    public long ChainId { get; }

    /// <summary>The genesis block header (number 0), with <see cref="AvalancheBlockHeader.StateRoot"/> populated.</summary>
    public AvalancheBlockHeader Header { get; }

    /// <summary>The genesis state root (= <see cref="AvalancheBlockHeader.StateRoot"/>).</summary>
    public Hash256 StateRoot { get; }

    /// <summary>The genesis block hash (<c>keccak256(RLP(header))</c>).</summary>
    public Hash256 Hash { get; }

    /// <summary>Parses the C-Chain genesis JSON and builds the genesis state root, header, and block hash.</summary>
    public static AvalancheCChainGenesis FromJson(ReadOnlyMemory<byte> utf8Json)
    {
        using JsonDocument doc = JsonDocument.Parse(utf8Json);
        JsonElement root = doc.RootElement;

        long chainId = root.GetProperty("config").GetProperty("chainId").GetInt64();

        Hash256 stateRoot = BuildStateRoot(root.GetProperty("alloc"));

        AvalancheBlockHeader header = new(
            parentHash: Keccak.Zero,
            unclesHash: Keccak.OfAnEmptySequenceRlp,
            beneficiary: new Address(GetString(root, "coinbase", "0x0000000000000000000000000000000000000000")),
            difficulty: ParseUInt256(GetString(root, "difficulty", "0x0")),
            number: 0,
            gasLimit: ParseUInt64(GetString(root, "gasLimit", "0x0")),
            timestamp: ParseUInt64(GetString(root, "timestamp", "0x0")),
            extraData: Bytes.FromHexString(GetString(root, "extraData", "0x")))
        {
            StateRoot = stateRoot,
            TxRoot = Keccak.EmptyTreeHash,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            Bloom = Bloom.Empty,
            GasUsed = 0,
            MixHash = new Hash256(GetString(root, "mixHash", "0x0000000000000000000000000000000000000000000000000000000000000000")),
            Nonce = ParseUInt64(GetString(root, "nonce", "0x0")),
            // Coreth writes a zero ExtDataHash at genesis (not the empty-extData keccak).
            ExtDataHash = Keccak.Zero
        };

        if (TryGetString(root, "baseFeePerGas", out string? baseFee))
        {
            header.BaseFeePerGas = ParseUInt256(baseFee!);
        }

        Hash256 hash = AvalancheHeaderDecoder.Instance.ComputeHash(header);
        return new AvalancheCChainGenesis(chainId, header, stateRoot, hash);
    }

    private static Hash256 BuildStateRoot(JsonElement alloc)
    {
        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);

        foreach (JsonProperty entry in alloc.EnumerateObject())
        {
            Address address = new(entry.Name);
            JsonElement account = entry.Value;

            if (account.TryGetProperty("storage", out JsonElement storage)
                && storage.ValueKind == JsonValueKind.Object
                && storage.EnumerateObject().MoveNext())
            {
                throw new NotSupportedException(
                    $"Genesis alloc account {entry.Name} carries storage, which is not yet supported.");
            }

            ulong nonce = TryGetString(account, "nonce", out string? n) ? ParseUInt64(n!) : 0;
            UInt256 balance = TryGetString(account, "balance", out string? b) ? ParseUInt256(b!) : UInt256.Zero;
            byte[] code = TryGetString(account, "code", out string? c) ? Bytes.FromHexString(c!) : [];
            Hash256 codeHash = code.Length > 0 ? Keccak.Compute(code) : Keccak.OfAnEmptyString;

            AvalancheStateAccount stateAccount = new(
                nonce,
                balance,
                Keccak.EmptyTreeHash.BytesToArray(),
                codeHash.BytesToArray(),
                isMultiCoin: false);

            tree.Set(Keccak.Compute(address.Bytes).Bytes, AvalancheStateAccountDecoder.Instance.Encode(stateAccount));
        }

        tree.Commit();
        return tree.RootHash;
    }

    private static string GetString(JsonElement obj, string name, string fallback) =>
        obj.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? fallback
            : fallback;

    private static bool TryGetString(JsonElement obj, string name, out string? value)
    {
        if (obj.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return value is not null;
        }

        value = null;
        return false;
    }

    private static ulong ParseUInt64(string hex)
    {
        ReadOnlySpan<char> span = StripPrefix(hex);
        return span.IsEmpty ? 0UL : ulong.Parse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static UInt256 ParseUInt256(string hex)
    {
        ReadOnlySpan<char> span = StripPrefix(hex);
        if (span.IsEmpty)
        {
            return UInt256.Zero;
        }

        // Hex quantities may be odd-length (e.g. "0x0"); left-pad a nibble so byte parsing is unambiguous.
        string clean = (span.Length & 1) == 0 ? span.ToString() : "0" + span.ToString();
        byte[] bytes = Bytes.FromHexString(clean);
        return new UInt256(bytes, isBigEndian: true);
    }

    private static ReadOnlySpan<char> StripPrefix(string hex)
    {
        ReadOnlySpan<char> span = hex.AsSpan();
        return span.StartsWith("0x") ? span[2..] : span;
    }
}
