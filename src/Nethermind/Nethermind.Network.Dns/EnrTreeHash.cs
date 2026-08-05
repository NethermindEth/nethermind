// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Dns;

/// <summary>
/// EIP-1459 serves every subtree entry from the subdomain base32(keccak256(entry)[..16]).
/// The root signature covers the enrtree-root entry only, so this hash chain is the sole binding
/// between the signed root and the branch and ENR entries a client consumes. Without it any
/// resolver, poisoned cache or plaintext-DNS middlebox can substitute arbitrary node records.
/// </summary>
internal static class EnrTreeHash
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// Publishers may abbreviate a label, so only a prefix of the hash is bound. The accepted range matches
    /// the reference implementation's <c>minHashLength</c>/<c>isValidHash</c>: fewer than 12 bytes leaves a
    /// prefix short enough to collide, and no label carries more than a keccak256 hash.
    /// </summary>
    private const int MinHashLength = 12;
    private const int MaxHashLength = 32;

    /// <summary>
    /// Checks that <paramref name="entryText"/> hashes to the <paramref name="subdomain"/> label it was served from.
    /// </summary>
    public static bool Matches(string subdomain, string entryText)
    {
        Span<byte> expected = stackalloc byte[MaxHashLength];
        if (!TryDecodeBase32(subdomain, expected, out int expectedLength) || expectedLength < MinHashLength)
        {
            return false;
        }

        ValueHash256 actual = ValueKeccak.Compute(Encoding.UTF8.GetBytes(entryText));
        return actual.Bytes[..expectedLength].SequenceEqual(expected[..expectedLength]);
    }

    /// <summary>
    /// Decodes the unpadded standard base32 alphabet used by EIP-1459 hash labels. Leftover bits of an
    /// incomplete trailing group are discarded, matching the reference implementation.
    /// </summary>
    private static bool TryDecodeBase32(string encoded, Span<byte> destination, out int length)
    {
        length = 0;
        if (encoded.Length * 5 / 8 > destination.Length)
        {
            return false;
        }

        int buffer = 0;
        int bits = 0;
        foreach (char c in encoded)
        {
            int value = Base32Alphabet.IndexOf(c);
            if (value < 0)
            {
                return false;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                destination[length++] = (byte)(buffer >> bits);
            }
        }

        return true;
    }
}
