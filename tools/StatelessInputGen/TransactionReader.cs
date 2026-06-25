// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;

namespace Nethermind.StatelessInputGen;

/// <summary>
/// Parses Ethereum transactions from the alloy-style camelCase JSON used by
/// <c>StatelessValidationFixture.block.body.transactions</c> into Nethermind
/// <see cref="Transaction"/> objects.
///
/// Supports all currently-deployed tx types:
/// <list type="bullet">
///   <item>0x0 — Legacy (pre-EIP-2930)</item>
///   <item>0x1 — EIP-2930 access-list</item>
///   <item>0x2 — EIP-1559 fee-market</item>
///   <item>0x3 — EIP-4844 blob</item>
///   <item>0x4 — EIP-7702 set-code</item>
/// </list>
/// </summary>
internal static class TransactionReader
{
    internal static Transaction Read(JsonElement tx)
    {
        TxType type = ParseTxType(tx);
        return type switch
        {
            TxType.Legacy => ReadLegacy(tx),
            TxType.AccessList => ReadAccessList(tx),
            TxType.EIP1559 => Read1559(tx),
            TxType.Blob => ReadBlob(tx),
            TxType.SetCode => ReadSetCode(tx),
            _ => throw new NotSupportedException($"Transaction type {type} not supported"),
        };
    }

    private static Transaction ReadLegacy(JsonElement tx) => new()
    {
        Type = TxType.Legacy,
        ChainId = TryGetUInt64(tx, "chainId"),
        Nonce = ReadUInt64(tx, "nonce"),
        GasPrice = ReadUInt256(tx, "gasPrice"),
        GasLimit = ReadUInt64(tx, "gas"),
        To = ReadOptionalAddress(tx, "to"),
        Value = ReadUInt256(tx, "value"),
        Data = ReadBytes(tx, "input"),
        Signature = ReadSignature(tx, withYParity: false),
    };

    private static Transaction ReadAccessList(JsonElement tx) => new()
    {
        Type = TxType.AccessList,
        ChainId = ReadUInt64(tx, "chainId"),
        Nonce = ReadUInt64(tx, "nonce"),
        GasPrice = ReadUInt256(tx, "gasPrice"),
        GasLimit = ReadUInt64(tx, "gas"),
        To = ReadOptionalAddress(tx, "to"),
        Value = ReadUInt256(tx, "value"),
        Data = ReadBytes(tx, "input"),
        AccessList = ReadAccessListField(tx),
        Signature = ReadSignature(tx, withYParity: true),
    };

    private static Transaction Read1559(JsonElement tx) => new()
    {
        Type = TxType.EIP1559,
        ChainId = ReadUInt64(tx, "chainId"),
        Nonce = ReadUInt64(tx, "nonce"),
        GasPrice = ReadUInt256(tx, "maxPriorityFeePerGas"),
        DecodedMaxFeePerGas = ReadUInt256(tx, "maxFeePerGas"),
        GasLimit = ReadUInt64(tx, "gas"),
        To = ReadOptionalAddress(tx, "to"),
        Value = ReadUInt256(tx, "value"),
        Data = ReadBytes(tx, "input"),
        AccessList = ReadAccessListField(tx),
        Signature = ReadSignature(tx, withYParity: true),
    };

    private static Transaction ReadBlob(JsonElement tx) => new()
    {
        Type = TxType.Blob,
        ChainId = ReadUInt64(tx, "chainId"),
        Nonce = ReadUInt64(tx, "nonce"),
        GasPrice = ReadUInt256(tx, "maxPriorityFeePerGas"),
        DecodedMaxFeePerGas = ReadUInt256(tx, "maxFeePerGas"),
        GasLimit = ReadUInt64(tx, "gas"),
        To = ReadOptionalAddress(tx, "to"),
        Value = ReadUInt256(tx, "value"),
        Data = ReadBytes(tx, "input"),
        AccessList = ReadAccessListField(tx),
        MaxFeePerBlobGas = ReadUInt256(tx, "maxFeePerBlobGas"),
        BlobVersionedHashes = ReadBlobVersionedHashes(tx),
        Signature = ReadSignature(tx, withYParity: true),
    };

    private static Transaction ReadSetCode(JsonElement tx) => new()
    {
        Type = TxType.SetCode,
        ChainId = ReadUInt64(tx, "chainId"),
        Nonce = ReadUInt64(tx, "nonce"),
        GasPrice = ReadUInt256(tx, "maxPriorityFeePerGas"),
        DecodedMaxFeePerGas = ReadUInt256(tx, "maxFeePerGas"),
        GasLimit = ReadUInt64(tx, "gas"),
        To = ReadOptionalAddress(tx, "to"),
        Value = ReadUInt256(tx, "value"),
        Data = ReadBytes(tx, "input"),
        AccessList = ReadAccessListField(tx),
        AuthorizationList = ReadAuthorizationList(tx),
        Signature = ReadSignature(tx, withYParity: true),
    };

    private static TxType ParseTxType(JsonElement tx)
    {
        if (!tx.TryGetProperty("type", out JsonElement typeEl))
            return TxType.Legacy;
        ulong v = ParseUInt64(typeEl.GetString()!);
        return (TxType)v;
    }

    private static AccessList? ReadAccessListField(JsonElement tx)
    {
        if (!tx.TryGetProperty("accessList", out JsonElement al) || al.ValueKind != JsonValueKind.Array)
            return null;
        AccessList.Builder builder = new();
        foreach (JsonElement entry in al.EnumerateArray())
        {
            builder.AddAddress(new Address(entry.GetProperty("address").GetString()!));
            if (entry.TryGetProperty("storageKeys", out JsonElement keys) && keys.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement key in keys.EnumerateArray())
                    builder.AddStorage(ParseUInt256(key.GetString()!));
            }
        }
        return builder.Build();
    }

    private static byte[]?[]? ReadBlobVersionedHashes(JsonElement tx)
    {
        if (!tx.TryGetProperty("blobVersionedHashes", out JsonElement hashes) || hashes.ValueKind != JsonValueKind.Array)
            return null;
        byte[]?[] result = new byte[hashes.GetArrayLength()][];
        int i = 0;
        foreach (JsonElement h in hashes.EnumerateArray())
        {
            byte[] hash = ParseHex(h.GetString()!);
            // EIP-4844 §Definitions: each versioned hash is a 32-byte value
            // (version byte + 31-byte KZG commitment digest). A non-32-byte
            // entry indicates a malformed fixture.
            if (hash.Length != Hash256.Size)
                throw new InvalidDataException(
                    $"Blob versioned hash #{i} has length {hash.Length}; EIP-4844 requires exactly {Hash256.Size} bytes.");
            result[i++] = hash;
        }
        return result;
    }

    private static AuthorizationTuple[]? ReadAuthorizationList(JsonElement tx)
    {
        if (!tx.TryGetProperty("authorizationList", out JsonElement al) || al.ValueKind != JsonValueKind.Array)
            return null;
        AuthorizationTuple[] result = new AuthorizationTuple[al.GetArrayLength()];
        int i = 0;
        foreach (JsonElement entry in al.EnumerateArray())
        {
            UInt256 chainId = ReadUInt256(entry, "chainId");
            Address codeAddress = new(entry.GetProperty("address").GetString()!);
            ulong nonce = ReadUInt64(entry, "nonce");
            byte yParity = (byte)ReadUInt64(entry, "yParity");
            UInt256 r = ReadUInt256(entry, "r");
            UInt256 s = ReadUInt256(entry, "s");
            result[i++] = new AuthorizationTuple(chainId, codeAddress, nonce, yParity, r, s);
        }
        return result;
    }

    private static Signature ReadSignature(JsonElement tx, bool withYParity)
    {
        UInt256 r = ReadUInt256(tx, "r");
        UInt256 s = ReadUInt256(tx, "s");
        ulong v;
        if (withYParity && tx.TryGetProperty("yParity", out JsonElement yp))
            v = ParseUInt64(yp.GetString()!) + Signature.VOffset;
        else
            v = ReadUInt64(tx, "v");
        return new Signature(r, s, v);
    }

    private static Address? ReadOptionalAddress(JsonElement tx, string field)
    {
        if (!tx.TryGetProperty(field, out JsonElement el) || el.ValueKind == JsonValueKind.Null)
            return null;
        string s = el.GetString()!;
        return string.IsNullOrEmpty(s) || s == "0x" ? null : new Address(s);
    }

    private static byte[] ReadBytes(JsonElement tx, string field)
        => tx.TryGetProperty(field, out JsonElement el) && el.ValueKind != JsonValueKind.Null
            ? ParseHex(el.GetString()!)
            : [];

    private static UInt256 ReadUInt256(JsonElement tx, string field)
        => ParseUInt256(tx.GetProperty(field).GetString()!);

    private static ulong ReadUInt64(JsonElement tx, string field)
        => ParseUInt64(tx.GetProperty(field).GetString()!);

    private static ulong? TryGetUInt64(JsonElement tx, string field)
        => tx.TryGetProperty(field, out JsonElement el) && el.ValueKind != JsonValueKind.Null
            ? ParseUInt64(el.GetString()!)
            : null;

    private static ulong ParseUInt64(string hex)
    {
        ReadOnlySpan<char> s = hex.AsSpan();
        if (s.StartsWith("0x") || s.StartsWith("0X")) s = s[2..];
        if (s.Length == 0) return 0;
        return ulong.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static UInt256 ParseUInt256(string hex)
    {
        ReadOnlySpan<char> s = hex.AsSpan();
        if (s.StartsWith("0x") || s.StartsWith("0X")) s = s[2..];
        if (s.Length == 0) return UInt256.Zero;
        return UInt256.Parse(s, NumberStyles.HexNumber);
    }

    private static byte[] ParseHex(string hex)
    {
        ReadOnlySpan<char> s = hex.AsSpan();
        if (s.StartsWith("0x") || s.StartsWith("0X")) s = s[2..];
        if (s.Length == 0) return [];
        return Convert.FromHexString(s);
    }
}
