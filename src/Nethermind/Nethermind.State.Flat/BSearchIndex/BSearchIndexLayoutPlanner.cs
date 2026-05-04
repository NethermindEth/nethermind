// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.BSearchIndex;

/// <summary>
/// Decides the optimal index-node layout — common-key-prefix length plus
/// (KeyType, KeySlotSize) — for a set of separators in a single pass.
///
/// Used by callers (e.g. <c>HsstIndexBuilder</c>) that already hold the separator
/// data in flight; the resulting prefix length and key-type are then passed to
/// <see cref="BSearchIndexWriter{TWriter}"/> as construction options. This way
/// the strip-vs-no-strip decision and the layout decision are made together,
/// with the layout chosen against post-strip (effective) lengths so a node
/// whose mixed-length keys collapse to fixed-width suffixes after stripping
/// gets the tightest layout the data supports.
/// </summary>
internal static class BSearchIndexLayoutPlanner
{
    /// <summary>
    /// Cap on the common-key-prefix length stored in node metadata. The trailing
    /// MetadataLength byte limits the metadata block to 255 bytes; 128 leaves
    /// comfortable headroom for flags + LEB128 counts + base offset + the prefix.
    /// </summary>
    public const int MaxCommonKeyPrefixLen = 128;

    /// <summary>
    /// Compute the longest common prefix and the tightest KeyType+KeySlotSize for
    /// a node whose separators are described by parallel <paramref name="offsets"/>
    /// and <paramref name="lengths"/> spans into <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Backing byte buffer holding all separators contiguously.</param>
    /// <param name="offsets">Per-entry start offset into <paramref name="buffer"/>.</param>
    /// <param name="lengths">Per-entry separator length. Length determines count.</param>
    /// <param name="commonKeyPrefixLen">Out: post-gating LCP. 0 if not worth stripping.</param>
    /// <param name="keyType">Out: 0=Variable, 1=Uniform, 2=UniformWithLen.</param>
    /// <param name="keySlotSize">Out: post-strip slot size for Uniform/UniformWithLen; 0 for Variable.</param>
    public static void Plan(
        ReadOnlySpan<byte> buffer,
        ReadOnlySpan<int> offsets,
        ReadOnlySpan<int> lengths,
        out int commonKeyPrefixLen,
        out int keyType,
        out int keySlotSize)
    {
        int count = lengths.Length;
        if (count == 0)
        {
            commonKeyPrefixLen = 0;
            keyType = 0;
            keySlotSize = 0;
            return;
        }

        int firstLen = lengths[0];
        int minLen = firstLen;
        int maxLen = firstLen;
        bool allSameLen = true;
        int secondLen = -1;
        bool allSameLenExceptFirst = count >= 2;
        int lcp = firstLen;

        ReadOnlySpan<byte> first = firstLen > 0 ? buffer.Slice(offsets[0], firstLen) : default;

        for (int i = 1; i < count; i++)
        {
            int len = lengths[i];
            if (len < minLen) minLen = len;
            if (len > maxLen) maxLen = len;
            if (len != firstLen) allSameLen = false;
            if (i == 1) secondLen = len;
            else if (len != secondLen) allSameLenExceptFirst = false;
            if (lcp > 0)
            {
                int boundary = Math.Min(len, lcp);
                int common = first[..boundary]
                    .CommonPrefixLength(buffer.Slice(offsets[i], boundary));
                if (common < lcp) lcp = common;
            }
        }

        if (lcp > MaxCommonKeyPrefixLen) lcp = MaxCommonKeyPrefixLen;

        // Strip-gate: positive savings, no key collapses to empty.
        if (lcp == 0 || lcp >= minLen || lcp * (count - 1) - 1 <= 0)
            lcp = 0;

        // KeyType selection on effective (post-strip) lengths.
        int effFirstLen = firstLen - lcp;
        int effMaxLen = maxLen - lcp;
        int effSecondLen = secondLen < 0 ? 0 : secondLen - lcp;
        bool emptyFirst = firstLen == 0;

        if (emptyFirst && count > 1 && allSameLenExceptFirst && effSecondLen > 0)
        {
            // Intermediate-node niche: leftmost child has no separator (covers
            // everything before any explicit one) and every other separator has
            // the same length — store as UniformWithLen with slot = secondLen + 1.
            keyType = 2;
            keySlotSize = effSecondLen + 1;
        }
        else if (allSameLen && effFirstLen > 0)
        {
            keyType = 1;
            keySlotSize = effFirstLen;
        }
        else if (effMaxLen <= 3)
        {
            // Variable layout costs ≥3 bytes/entry overhead (2-byte offset table
            // entry + 1-byte LEB128 length); UniformWithLen wins for tiny suffixes.
            keyType = 2;
            keySlotSize = effMaxLen + 1;
        }
        else
        {
            keyType = 0;
            keySlotSize = 0;
        }

        commonKeyPrefixLen = lcp;
    }
}
