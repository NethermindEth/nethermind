// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class OverlayNodeRecordBufferTests
{
    private static readonly byte[] BeforePinLeaf = Bytes.FromHexString("0xc482201201");
    private static readonly byte[] AfterPinLeaf = Bytes.FromHexString("0xc482201202");
    private static readonly byte[] OverlayLeaf = Bytes.FromHexString("0xc482201203");

    [Test]
    public void Reads_global_records_below_the_pin_and_keeps_its_own_writes_private()
    {
        ForwardingNodeRecordBuffer global = new(FlatTestHelpers.NewTestBuffer());
        using IDisposable globalReadPin = global.PinAll();
        ulong beforePin = Append(global, BeforePinLeaf);

        OverlayNodeRecordBuffer overlay = new(global);
        ulong afterPin = Append(global, AfterPinLeaf);
        // The global buffer indexes the same root again, as when block processing returns storage to an earlier root.
        ulong reindexed = Append(global, BeforePinLeaf);
        int globalWrites = global.CompletedWrites;

        INodeRecordBufferPin overlayPin = overlay.PinHead();
        ulong written = Append(overlay, OverlayLeaf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(global.CompletedWrites, Is.EqualTo(globalWrites), "overlay writes stay out of the global buffer");
            Assert.That(written, Is.GreaterThan(afterPin), "overlay pointers follow the global pin");
            Assert.That(overlayPin.WriteBoundary, Is.GreaterThan(afterPin));
            Assert.That(overlay.TryLocate(beforePin, out NodeRecord beforePinRecord) && beforePinRecord.Rlp.SequenceEqual(BeforePinLeaf), Is.True);
            Assert.That(overlay.TryLocate(written, out NodeRecord writtenRecord) && writtenRecord.Rlp.SequenceEqual(OverlayLeaf), Is.True);
            Assert.That(overlay.TryLocate(afterPin, out _), Is.False, "global records written after the pin are not visible");
            Assert.That(overlay.FindRoot(null, ValueKeccak.Compute(BeforePinLeaf), ulong.MaxValue), Is.EqualTo(beforePin), "a root indexed again after the pin resolves to the entry visible at the pin");
            Assert.That(global.FindRoot(null, ValueKeccak.Compute(BeforePinLeaf), ulong.MaxValue), Is.EqualTo(reindexed));
            Assert.That(global.FindRoot(null, ValueKeccak.Compute(BeforePinLeaf), reindexed), Is.EqualTo(beforePin), "a bound skips the newer entries of the root");
            Assert.That(overlay.FindRoot(null, ValueKeccak.Compute(AfterPinLeaf), ulong.MaxValue), Is.Zero);
            Assert.That(overlay.FindRoot(null, ValueKeccak.Compute(OverlayLeaf), ulong.MaxValue), Is.EqualTo(written));
            Assert.That(global.FindRoot(null, ValueKeccak.Compute(OverlayLeaf), ulong.MaxValue), Is.Zero);
        }

        overlay.Dispose();
        Assert.That(global.OutstandingPins, Is.EqualTo(1), "a live overlay pin keeps the global pin");
        overlayPin.Dispose();
        Assert.That(global.OutstandingPins, Is.Zero);
        global.Dispose();
    }

    private static ulong Append(INodeRecordBuffer buffer, byte[] leaf)
    {
        ValueHash256 hash = ValueKeccak.Compute(leaf);
        ulong pointer = buffer.AppendRecordFromRlp(null, TreePath.Empty, hash, leaf, []);
        buffer.SetRoot(null, hash, pointer);
        return pointer;
    }
}
