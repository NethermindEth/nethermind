// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.PersistedSnapshots;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class PersistedSnapshotBuilderPagePaddingTests
{
    // (initialOffsetInPage, valueLength, expectedPad)
    // Pad rule: pad = 4096 - offsetInPage when value <= 4096 and offsetInPage != 0
    //           and offsetInPage + value > 4096; otherwise no padding.
    [TestCase(0, 100, 0, TestName = "PageStart_NoPad")]
    [TestCase(100, 200, 0, TestName = "FitsInPage_NoPad")]
    [TestCase(4000, 96, 0, TestName = "ExactlyEndsAtBoundary_NoPad")]
    [TestCase(4000, 200, 96, TestName = "Crosses_PadToNextPage")]
    [TestCase(1, 4096, 4095, TestName = "MaxValueWithLeadingByte_PadsToBoundary")]
    [TestCase(0, 5000, 0, TestName = "OversizeAtPageStart_NoPad")]
    [TestCase(500, 5000, 0, TestName = "OversizeMidPage_NoPadBecauseRulePrefersNotWastingPage")]
    public void WriteTrieNodeRlpPageAligned_PadsToKeepValueWithinSinglePage(
        int initialOffsetInPage, int valueLength, int expectedPad)
    {
        // Buffer large enough for any case under test, with a deliberate FirstOffset so the
        // writer position alone (without subtracting FirstOffset) would mis-classify the page.
        const long firstOffset = 123;
        byte[] backing = new byte[1 << 16];
        SpanBufferWriter writer = new(backing, firstOffset);

        // Advance writer to put us at `initialOffsetInPage` within a 4 KiB page.
        long pad0 = ((-(writer.Written - firstOffset)) & 4095L);
        writer.Advance((int)pad0);
        writer.Advance(initialOffsetInPage);

        long beforeValue = writer.Written;
        byte[] value = new byte[valueLength];
        for (int i = 0; i < valueLength; i++) value[i] = (byte)(i & 0xff);

        PersistedSnapshotBuilder.WriteTrieNodeRlpPageAligned(ref writer, value);

        long afterValue = writer.Written;
        Assert.That(afterValue - beforeValue, Is.EqualTo(expectedPad + valueLength),
            "writer should have advanced by pad + valueLength");

        long valueStart = beforeValue + expectedPad;
        long pageStart = (valueStart - firstOffset) & ~4095L;
        long offsetWithinPage = (valueStart - firstOffset) - pageStart;

        if (valueLength <= 4096)
        {
            Assert.That(offsetWithinPage + valueLength, Is.LessThanOrEqualTo(4096),
                "value must lie within a single 4 KiB page when length <= 4096");
        }

        // Value bytes are written intact at valueStart.
        Assert.That(backing.AsSpan((int)valueStart, valueLength).ToArray(), Is.EqualTo(value));
    }
}
