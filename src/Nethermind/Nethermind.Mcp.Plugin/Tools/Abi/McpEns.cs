// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Mcp.Plugin.Tools.Abi;

/// <summary>ENS name normalisation, ENSIP-1 namehash and ENSIP-10 DNS encoding.</summary>
/// <remarks>
/// Normalisation is a strict ASCII subset of ENSIP-15: ASCII letters are lowercased and labels may contain only
/// <c>a-z</c>, <c>0-9</c>, <c>-</c> and leading <c>_</c>. Names with non-ASCII characters (Unicode or emoji) are rejected rather than
/// hashed, because without full ENSIP-15/UTS-46 normalisation they could hash to a different node than the canonical name.
/// </remarks>
public static class McpEns
{
    /// <summary>The maximum accepted name length in characters.</summary>
    public const int MaxNameLength = 255;

    /// <summary>The maximum number of labels in a name.</summary>
    public const int MaxLabels = 16;

    /// <summary>The <c>addr(bytes32)</c> resolver selector.</summary>
    public static readonly byte[] AddrSelector = [0x3b, 0x3b, 0x57, 0xde];

    /// <summary>The <c>name(bytes32)</c> resolver selector (reverse records).</summary>
    public static readonly byte[] NameSelector = [0x69, 0x1f, 0x34, 0x31];

    /// <summary>The registry <c>resolver(bytes32)</c> selector.</summary>
    public static readonly byte[] ResolverSelector = [0x01, 0x78, 0xb8, 0xbf];

    /// <summary>The registry <c>owner(bytes32)</c> selector.</summary>
    public static readonly byte[] OwnerSelector = [0x02, 0x57, 0x1b, 0xe3];

    /// <summary>The ENSIP-10 <c>resolve(bytes,bytes)</c> selector, which is also its ERC-165 interface ID.</summary>
    public static readonly byte[] ResolveSelector = [0x90, 0x61, 0xb9, 0x23];

    /// <summary>The EIP-3668 <c>OffchainLookup(address,string[],bytes,bytes4,bytes)</c> error selector.</summary>
    public static readonly byte[] OffchainLookupSelector = [0x55, 0x6f, 0x18, 0x30];

    /// <summary>The Universal Resolver <c>reverse(bytes,uint256)</c> selector (ENSIP-19 primary names).</summary>
    public static readonly byte[] ReverseSelector = Keccak.Compute("reverse(bytes,uint256)").Bytes[..4].ToArray();

    /// <summary>The Universal Resolver <c>ResolverNotFound(bytes)</c> error selector.</summary>
    public static readonly byte[] ResolverNotFoundSelector = [0x77, 0x20, 0x9f, 0xe8];

    /// <summary>The Universal Resolver <c>ResolverNotContract(bytes,address)</c> error selector.</summary>
    public static readonly byte[] ResolverNotContractSelector = [0x1e, 0x95, 0x35, 0xf2];

    /// <summary>The Universal Resolver <c>UnsupportedResolverProfile(bytes4)</c> error selector.</summary>
    public static readonly byte[] UnsupportedResolverProfileSelector = [0x7b, 0x1c, 0x46, 0x1b];

    /// <summary>The Universal Resolver <c>ResolverError(bytes)</c> error selector.</summary>
    public static readonly byte[] ResolverErrorSelector = [0x95, 0xc0, 0xc7, 0x52];

    /// <summary>The Universal Resolver <c>ReverseAddressMismatch(string,bytes)</c> error selector.</summary>
    public static readonly byte[] ReverseAddressMismatchSelector = [0xef, 0x9c, 0x03, 0xce];

    /// <summary>The SLIP-44 coin type of Ether, used for ENSIP-19 reverse resolution on Ethereum.</summary>
    public const ulong EthCoinType = 60;

    /// <summary>Normalises <paramref name="name"/> (trim and ASCII lowercase) and validates every label.</summary>
    public static bool TryNormalize(string? name, [NotNullWhen(true)] out string? normalized, [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            error = "'name' is empty; pass an ENS name such as \"vitalik.eth\"";
            return false;
        }

        if (trimmed.Length > MaxNameLength)
        {
            error = $"'name' is {trimmed.Length} characters; the maximum is {MaxNameLength}";
            return false;
        }

        StringBuilder builder = new(trimmed.Length);
        foreach (char c in trimmed)
        {
            if (!char.IsAscii(c))
            {
                error = "'name' contains non-ASCII characters; Unicode/emoji names need full ENSIP-15 normalisation, which this tool does not implement";
                return false;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        string lower = builder.ToString();
        string[] labels = lower.Split('.');
        if (labels.Length > MaxLabels)
        {
            error = $"'name' has {labels.Length} labels; the maximum is {MaxLabels}";
            return false;
        }

        foreach (string label in labels)
        {
            if (!IsValidLabel(label, out error))
            {
                return false;
            }
        }

        normalized = lower;
        error = null;
        return true;
    }

    /// <summary>Computes the ENSIP-1 namehash of an already normalised name (the empty string is the root node).</summary>
    public static Hash256 NameHash(string normalizedName)
    {
        byte[] node = new byte[64];
        if (normalizedName.Length > 0)
        {
            string[] labels = normalizedName.Split('.');
            for (int i = labels.Length - 1; i >= 0; i--)
            {
                Keccak.Compute(Encoding.UTF8.GetBytes(labels[i])).Bytes.CopyTo(node.AsSpan(32));
                Keccak.Compute(node).Bytes.CopyTo(node.AsSpan(0, 32));
            }
        }

        return new Hash256(node.AsSpan(0, 32));
    }

    /// <summary>Encodes a normalised name in DNS wire format, as ENSIP-10 <c>resolve(bytes,bytes)</c> expects.</summary>
    public static byte[] DnsEncode(string normalizedName)
    {
        string[] labels = normalizedName.Split('.');
        List<byte> result = new(normalizedName.Length + 2);
        foreach (string label in labels)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(label);
            result.Add((byte)bytes.Length);
            result.AddRange(bytes);
        }

        result.Add(0);
        return [.. result];
    }

    /// <summary>Returns the reverse-resolution name of <paramref name="address"/>: lowercase hex without 0x, followed by <c>.addr.reverse</c>.</summary>
    public static string ReverseName(Address address) => address.ToString(false, false) + ".addr.reverse";

    private static bool IsValidLabel(string label, [NotNullWhen(false)] out string? error)
    {
        if (label.Length == 0)
        {
            error = "'name' has an empty label (leading, trailing or double dot)";
            return false;
        }

        if (label.Length > 63)
        {
            error = $"label '{label[..16]}...' is longer than 63 characters";
            return false;
        }

        for (int i = 0; i < label.Length; i++)
        {
            char c = label[i];
            bool valid = char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-' || (c == '_' && label[..i].All(static p => p == '_'));
            if (!valid)
            {
                error = c == '_'
                    ? $"label '{label}' has an underscore that is not at the start"
                    : $"label '{label}' contains '{c}'; allowed are a-z, 0-9, '-' and leading '_'";
                return false;
            }
        }

        if (label.Length >= 4 && label[2] == '-' && label[3] == '-')
        {
            error = $"label '{label}' has '--' at positions 3-4, which ENSIP-15 reserves for punycode";
            return false;
        }

        error = null;
        return true;
    }
}
