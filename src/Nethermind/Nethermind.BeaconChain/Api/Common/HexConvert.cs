// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Api.Common;

internal static class HexConvert
{
    public static bool TryParseHash(string s, out Hash256? hash)
    {
        hash = null;
        if (string.IsNullOrEmpty(s) || !s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.Length != 66)
        {
            return false;
        }

        try
        {
            hash = new Hash256(Bytes.FromHexString(s));
            return true;
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Parses a 0x-prefixed 48-byte BLS pubkey, the alternate form of a beacon-api <c>validator_id</c>.</summary>
    public static bool TryParsePubKey(string s, out BlsPublicKey pubkey)
    {
        pubkey = default;
        if (string.IsNullOrEmpty(s) || !s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.Length != 2 + BlsPublicKey.Length * 2)
        {
            return false;
        }

        try
        {
            pubkey = new BlsPublicKey(Bytes.FromHexString(s));
            return true;
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Parses a 0x-prefixed 96-byte BLS signature.</summary>
    public static bool TryParseSignature(string s, out BlsSignature signature)
    {
        signature = default;
        if (string.IsNullOrEmpty(s) || !s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.Length != 2 + BlsSignature.Length * 2)
        {
            return false;
        }

        try
        {
            signature = new BlsSignature(Bytes.FromHexString(s));
            return true;
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Parses a 0x-prefixed 20-byte execution address.</summary>
    public static bool TryParseAddress(string? s, [NotNullWhen(true)] out Address? address)
    {
        address = TryParseBytes(s, out byte[]? bytes) && bytes.Length == Address.Size ? new Address(bytes) : null;
        return address is not null;
    }

    /// <summary>Encodes an SSZ bitvector (LSB-first within each byte) as a <c>0x</c>-prefixed hex string.</summary>
    public static string BitVectorToHex(BitArray bits)
    {
        byte[] bytes = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return bytes.ToHexString(withZeroX: true);
    }

    /// <summary>Encodes an SSZ bitlist, its bits followed by the sentinel bit that marks its length, as a <c>0x</c>-prefixed hex string.</summary>
    public static string BitListToHex(BitArray bits)
    {
        byte[] bytes = new byte[bits.Length / 8 + 1];
        bits.CopyTo(bytes, 0);
        bytes[^1] |= (byte)(1 << (bits.Length % 8));
        return bytes.ToHexString(withZeroX: true);
    }

    /// <summary>Parses a <c>0x</c>-prefixed SSZ bitlist of at most <paramref name="limit"/> bits.</summary>
    public static bool TryParseBitList(string? s, int limit, [NotNullWhen(true)] out BitArray? bits)
    {
        bits = null;
        if (!TryParseBytes(s, out byte[]? bytes) || bytes.Length == 0 || bytes[^1] == 0) return false;
        int length = (bytes.Length - 1) * 8 + BitOperations.Log2(bytes[^1]);
        if (length > limit) return false;
        bits = new BitArray(bytes) { Length = length };
        return true;
    }

    /// <summary>Parses a <c>0x</c>-prefixed SSZ bitvector of <paramref name="length"/> bits, whose unused high bits must be zero.</summary>
    public static bool TryParseBitVector(string? s, int length, [NotNullWhen(true)] out BitArray? bits)
    {
        bits = null;
        if (!TryParseBytes(s, out byte[]? bytes) || bytes.Length != (length + 7) / 8 || (length % 8 != 0 && bytes[^1] >> (length % 8) != 0)) return false;
        bits = new BitArray(bytes) { Length = length };
        return true;
    }

    private static bool TryParseBytes(string? s, [NotNullWhen(true)] out byte[]? bytes)
    {
        bytes = null;
        if (string.IsNullOrEmpty(s) || !s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            bytes = Bytes.FromHexString(s);
            return true;
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException)
        {
            return false;
        }
    }
}
