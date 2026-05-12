// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.BSearchIndex;

/// <summary>
/// Decides the optimal index-node layout — common-key-prefix length plus
/// (KeyType, KeySlotSize) — from per-entry separator lengths and a pre-computed
/// cross-entry LCP.
///
/// Used by callers (e.g. <c>HsstIndexBuilder</c>) that already know each
/// separator's length and have the leaf-wide LCP available from their own state
/// (no byte content needed). The resulting prefix length and key-type are then
/// passed to <see cref="BSearchIndexWriter{TWriter}"/> as construction options,
/// with the layout chosen against post-strip (effective) lengths so a node whose
/// mixed-length keys collapse to fixed-width suffixes after stripping gets the
/// tightest layout the data supports.
/// </summary>
internal static class BSearchIndexLayoutPlanner
{
    /// <summary>
    /// Cap on the common-key-prefix length stored in node metadata. Bounded by
    /// the u8 prefix-length byte in the fixed footer; 128 keeps prefix blocks
    /// small enough that <see cref="HsstBTreeReader"/>'s footer probe-window
    /// reads them in one shot.
    /// </summary>
    public const int MaxCommonKeyPrefixLen = 128;

    /// <summary>
    /// Compute the tightest KeyType+KeySlotSize for a node whose separator lengths are
    /// supplied in <paramref name="lengths"/>, given the cross-entry LCP across those
    /// separators in <paramref name="crossEntryLcp"/>.
    /// </summary>
    /// <param name="lengths">Per-entry separator length. Length determines count.</param>
    /// <param name="crossEntryLcp">
    /// Cross-entry common-prefix-length across all separators (the chain-min of adjacent
    /// key LCPs over the entries this node covers). May exceed individual <paramref name="lengths"/>;
    /// the planner caps via <c>min(minLen, crossEntryLcp)</c>.
    /// </param>
    /// <param name="keyLength">
    /// Per-key byte budget — the uniform key length declared by the HSST. Used to decide
    /// whether the planner can widen short uniform separators up to a 4-byte slot
    /// (Uniform slot=4 is SIMD-eligible via uint32 LE compare). Widening only fires when
    /// the post-strip total <c>prefixLen + keySlotSize</c> stays within this budget.
    /// </param>
    /// <param name="commonKeyPrefixLen">Out: post-gating LCP. 0 if not worth stripping.</param>
    /// <param name="keyType">Out: 0=Variable, 1=Uniform, 2=UniformWithLen.</param>
    /// <param name="keySlotSize">Out: post-strip slot size for Uniform/UniformWithLen; 0 for Variable.</param>
    /// <param name="keyLittleEndian">
    /// Out: when true, callers should set <c>BSearchIndexMetadata.IsKeyLittleEndian</c> so each
    /// fixed-width key slot is byte-reversed on disk (Flags bit 5). Set only for the SIMD-eligible
    /// shape: Uniform with <paramref name="keySlotSize"/> ∈ {2,4,8}.
    /// </param>
    public static void Plan(
        ReadOnlySpan<int> lengths,
        int crossEntryLcp,
        int keyLength,
        out int commonKeyPrefixLen,
        out int keyType,
        out int keySlotSize,
        out bool keyLittleEndian,
        bool disablePrefix = false)
    {
        int count = lengths.Length;
        if (count == 0)
        {
            commonKeyPrefixLen = 0;
            keyType = 0;
            keySlotSize = 0;
            keyLittleEndian = false;
            return;
        }

        int firstLen = lengths[0];
        int minLen = firstLen;
        int maxLen = firstLen;
        bool allSameLen = true;
        int secondLen = -1;
        bool allSameLenExceptFirst = count >= 2;

        for (int i = 1; i < count; i++)
        {
            int len = lengths[i];
            if (len < minLen) minLen = len;
            if (len > maxLen) maxLen = len;
            if (len != firstLen) allSameLen = false;
            if (i == 1) secondLen = len;
            else if (len != secondLen) allSameLenExceptFirst = false;
        }

        // Slot widening: when every input separator fits in 4 bytes (maxLen ≤ 4) AND every
        // key has ≥ 4 bytes available, treat the inputs as 4-byte for lcp + layout
        // selection. Works for both uniform-length and mixed-length inputs — after this
        // step the planner proceeds as if all separators were uniform 4 bytes. The
        // caller's AddKey must then provide 4 bytes per entry (read from the key's data
        // section, not the natural sep length). This is the SIMD-eligible Uniform slot=4 /
        // uint32 LE path. The strip-gate below may still pull lcp > 0 in, dropping slot
        // to 1/2/3 for non-trivial crossEntryLcp.
        if (firstLen > 0 && maxLen <= 4 && keyLength >= 4)
        {
            firstLen = 4;
            minLen = 4;
            maxLen = 4;
            if (secondLen >= 0) secondLen = 4;
            allSameLen = true;
            allSameLenExceptFirst = count >= 2;
        }

        int lcp = Math.Min(minLen, crossEntryLcp);
        if (lcp > MaxCommonKeyPrefixLen) lcp = MaxCommonKeyPrefixLen;

        // Strip-gate: positive savings, no key collapses to empty.
        if (lcp == 0 || lcp >= minLen || lcp * (count - 1) - 1 <= 0)
            lcp = 0;

        if (disablePrefix) lcp = 0;

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
            // Variable layout costs 4 bytes/entry (prefixArr 2B + offsetArr 2B, no sentinel) —
            // UniformWithLen wins for tiny suffixes since each slot is contiguous and
            // SIMD-scannable, with smaller per-entry overhead at maxLen ≤ 3.
            keyType = 2;
            keySlotSize = effMaxLen + 1;
        }
        else
        {
            keyType = 0;
            keySlotSize = 0;
        }

        commonKeyPrefixLen = lcp;
        // Auto-enable LE storage where the SIMD/integer-compare floor scan can exploit it:
        // Uniform 2/4/8, UniformWithLen slotSize=4, and Variable (prefixArr is uniformly 2B/slot).
        keyLittleEndian =
            keyType == 0 ||
            (keyType == 1 && keySlotSize is 2 or 4 or 8) ||
            (keyType == 2 && keySlotSize == 4);
    }
}
