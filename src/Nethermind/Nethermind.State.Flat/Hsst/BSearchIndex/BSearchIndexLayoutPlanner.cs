// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.BSearchIndex;

/// <summary>
/// Decides the optimal index-node layout — common-key-prefix length plus
/// (KeyType, KeySlotSize) — from per-entry separator lengths and a pre-computed
/// cross-entry LCP.
///
/// Used by callers (e.g. <c>HsstBTreeBuilder</c>) that already know each
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
    /// whether the planner can widen short uniform separators up to a 4-byte slot (Uniform
    /// slot=4 is SIMD-eligible via uint32 LE compare) or an 8-byte slot (slot=8 via uint64
    /// LE compare). Widening only fires when the post-strip total
    /// <c>prefixLen + keySlotSize</c> stays within this budget.
    /// </param>
    /// <param name="commonKeyPrefixLen">Out: post-gating LCP. 0 if not worth stripping.</param>
    /// <param name="keyType">Out: 0=Variable, 1=Uniform.</param>
    /// <param name="keySlotSize">Out: post-strip slot size for Uniform; 0 for Variable.</param>
    /// <param name="keyLittleEndian">
    /// Out: when true, callers should set <c>BSearchIndexMetadata.IsKeyLittleEndian</c> so each
    /// fixed-width key slot is byte-reversed on disk (Flags bit 5). Set for the SIMD-eligible
    /// shapes: Uniform with <paramref name="keySlotSize"/> ∈ {2,4,8} and Variable (whose 2-byte
    /// prefixArr is uniformly LE-encoded).
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

        PlanFromProfile(
            count, firstLen, secondLen, minLen, maxLen, allSameLen, allSameLenExceptFirst,
            crossEntryLcp, keyLength,
            out commonKeyPrefixLen, out keyType, out keySlotSize, out keyLittleEndian,
            disablePrefix);
    }

    /// <summary>
    /// Profile-based overload of <see cref="Plan"/>. Takes the per-entry-length summary
    /// directly so callers that already maintain the profile incrementally (e.g. the
    /// HSST leaf-merger probing whether two adjacent splits coalesce into a single
    /// node) can re-decide layout without rescanning a <c>lengths</c> span.
    /// </summary>
    /// <param name="count">Entry count. Must be &gt; 0.</param>
    /// <param name="firstLen">Length of entry 0's separator.</param>
    /// <param name="secondLen">Length of entry 1's separator, or -1 if <paramref name="count"/> &lt; 2.</param>
    /// <param name="minLen">Minimum length across all entries.</param>
    /// <param name="maxLen">Maximum length across all entries.</param>
    /// <param name="allSameLen">True iff every entry's length equals <paramref name="firstLen"/>.</param>
    /// <param name="allSameLenExceptFirst">True iff <paramref name="count"/> &gt;= 2 and entries [1..] all equal <paramref name="secondLen"/>.</param>
    internal static void PlanFromProfile(
        int count,
        int firstLen, int secondLen, int minLen, int maxLen,
        bool allSameLen, bool allSameLenExceptFirst,
        int crossEntryLcp, int keyLength,
        out int commonKeyPrefixLen,
        out int keyType,
        out int keySlotSize,
        out bool keyLittleEndian,
        bool disablePrefix = false)
    {
        // Slot widening: when every natural separator fits in {2, 4, 8} and the keyLength
        // budget allows, pretend they're all `target` bytes — the builder pads each slot
        // from key data. The downstream Uniform branch then snaps to a power-of-2 SIMD
        // slot when the post-strip budget allows; cases where the budget is too tight
        // keep a non-SIMD slot rather than sacrificing lcp.
        int target = firstLen > 0 ? WidenedSlotWidth(maxLen, keyLength) : maxLen;
        if (target > maxLen)
        {
            firstLen = target;
            minLen = target;
            maxLen = target;
            if (secondLen >= 0) secondLen = target;
            allSameLen = true;
            allSameLenExceptFirst = count >= 2;
        }

        // BSearchIndexWriter takes `keySlotSize` bytes per entry from
        // currKey.Slice(prefixLen, slot) for Uniform layouts, padding from key data
        // past each entry's natural separator length when the slot exceeds it. For
        // Variable layouts the writer instead slices `currKey.Slice(prefixLen,
        // sepLength - prefixLen)` per entry, which requires lcp ≤ every sep length
        // (i.e. lcp ≤ minLen) or the slice goes negative. Since the planner picks
        // Uniform-vs-Variable AFTER fixing lcp, we conservatively clamp to minLen
        // even though Uniform alone could safely take lcp = crossEntryLcp (writer
        // pads short slots from key data past the natural sep). The missed
        // optimization fires only when entry 0's LCP with the previous leaf's last
        // key is shorter than the leaf-internal crossEntryLcp.
        //
        // Then clamp by keyLength - 1 to reserve at least one byte per slot, and by
        // the header's u8 prefix-length field.
        int lcp = Math.Min(crossEntryLcp, minLen);
        if (lcp > keyLength - 1) lcp = keyLength - 1;
        if (lcp > MaxCommonKeyPrefixLen) lcp = MaxCommonKeyPrefixLen;

        // Strip-gate: strictly positive net savings.
        // Block cost = 1 + lcp; per-entry saving = lcp; net = lcp * (count - 1) - 1.
        if (lcp <= 0 || lcp * (count - 1) - 1 <= 0)
            lcp = 0;

        if (disablePrefix) lcp = 0;

        // KeyType selection on effective (post-strip) lengths. Two outcomes:
        //   * Uniform: every slot is the same fixed width; mixed-length entries pad
        //     from the key data section past the natural separator.
        //   * Variable: only chosen when effMaxLen > 8 and lengths actually vary,
        //     where padding every entry up to effMaxLen would cost more than the
        //     Variable layout's 4 B/entry overhead. The splitter's `gap > 8` quality
        //     gate keeps within-leaf length variance small, so this path is rare.
        int effMaxLen = maxLen - lcp;

        if (allSameLen || effMaxLen <= 8)
        {
            keyType = 1;
            int budget = keyLength - lcp;
            keySlotSize =
                effMaxLen <= 2 && budget >= 2 ? 2 :
                effMaxLen <= 4 && budget >= 4 ? 4 :
                effMaxLen <= 8 && budget >= 8 ? 8 :
                effMaxLen;
        }
        else
        {
            keyType = 0;
            keySlotSize = 0;
        }

        commonKeyPrefixLen = lcp;
        // Auto-enable LE storage where the SIMD/integer-compare floor scan can exploit it:
        // Uniform 2/4/8, and Variable (prefixArr is uniformly 2B/slot).
        keyLittleEndian =
            keyType == 0 ||
            (keyType == 1 && keySlotSize is 2 or 4 or 8);
    }

    /// <summary>
    /// Slot-widening rule shared by <see cref="PlanFromProfile"/> and callers that size a
    /// node before planning it (e.g. <c>HsstBTreeBuilder</c>'s split heuristic): the
    /// SIMD-eligible Uniform slot width a node whose longest separator is
    /// <paramref name="maxLen"/> bytes is widened up to — {2, 4, 8} when the per-key
    /// <paramref name="keyLength"/> budget allows — or <paramref name="maxLen"/> unchanged
    /// when no widening applies (longer than 8 bytes, or the budget is too tight).
    /// </summary>
    internal static int WidenedSlotWidth(int maxLen, int keyLength) =>
        maxLen <= 2 && keyLength >= 2 ? 2 :
        maxLen <= 4 && keyLength >= 4 ? 4 :
        maxLen <= 8 && keyLength >= 8 ? 8 :
        maxLen;

}
