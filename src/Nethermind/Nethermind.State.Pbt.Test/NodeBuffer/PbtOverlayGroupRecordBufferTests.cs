// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test.NodeBuffer;

public class PbtOverlayGroupRecordBufferTests
{
    private static readonly byte[] BeforePinPayload = Bytes.FromHexString("0x01");
    private static readonly byte[] AfterPinPayload = Bytes.FromHexString("0x02");
    private static readonly byte[] OverlayPayload = Bytes.FromHexString("0x03");

    [Test]
    public void Reads_global_records_below_the_pin_and_keeps_its_own_writes_private()
    {
        ForwardingGroupRecordBuffer global = new(new PbtGroupRecordBuffer(0, 64 * 1024, PbtGroupRecordBuffer.DefaultLiveGenerations));
        using IDisposable globalReadPin = global.PinAll();
        ulong beforePin = Append(global, BeforePinPayload);

        PbtOverlayGroupRecordBuffer overlay = new(global);
        ulong afterPin = Append(global, AfterPinPayload);
        int globalWrites = global.CompletedWrites;

        IPbtGroupRecordBufferPin overlayPin = overlay.PinHead();
        ulong written = Append(overlay, OverlayPayload);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(global.CompletedWrites, Is.EqualTo(globalWrites), "overlay writes stay out of the global buffer");
            Assert.That(written, Is.GreaterThan(afterPin), "overlay pointers follow the global pin");
            Assert.That(overlayPin.WriteBoundary, Is.GreaterThan(afterPin));
            Assert.That(overlay.TryLocate(beforePin, out PbtGroupRecord beforePinRecord) && beforePinRecord.Payload.Span.SequenceEqual(BeforePinPayload), Is.True);
            Assert.That(overlay.TryLocate(written, out PbtGroupRecord writtenRecord) && writtenRecord.Payload.Span.SequenceEqual(OverlayPayload), Is.True);
            Assert.That(overlay.TryLocate(afterPin, out _), Is.False, "global records written after the pin are not visible");
            Assert.That(overlay.FindRoot(ValueKeccak.Compute(BeforePinPayload)), Is.EqualTo(beforePin));
            Assert.That(overlay.FindRoot(ValueKeccak.Compute(AfterPinPayload)), Is.Zero);
            Assert.That(overlay.FindRoot(ValueKeccak.Compute(OverlayPayload)), Is.EqualTo(written));
            Assert.That(global.FindRoot(ValueKeccak.Compute(OverlayPayload)), Is.Zero);
        }

        overlay.Dispose();
        Assert.That(global.OutstandingPins, Is.EqualTo(1), "a live overlay pin keeps the global pin");
        overlayPin.Dispose();
        Assert.That(global.OutstandingPins, Is.Zero);
        global.Dispose();
    }

    private static ulong Append(IPbtGroupRecordBuffer buffer, byte[] payload)
    {
        ValueHash256 hash = ValueKeccak.Compute(payload);
        ulong pointer = buffer.Append(default, payload, default);
        buffer.SetRoot(hash, pointer);
        return pointer;
    }

    private sealed class ForwardingGroupRecordBuffer(IPbtGroupRecordBuffer inner) : IPbtGroupRecordBuffer
    {
        public int CompletedWrites;
        public int OutstandingPins;

        public bool TryLocate(ulong pointer, out PbtGroupRecord record) => inner.TryLocate(pointer, out record);
        public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => new ForwardedWriteBatch(this, inner.StartWriteBatch(alignedTotalLength));

        public ulong FindRoot(in ValueHash256 root) => inner.FindRoot(root);
        public void SetRoot(in ValueHash256 root, ulong rootPointer) => inner.SetRoot(root, rootPointer);
        public IPbtGroupRecordBufferPin PinHead() => Wrap(inner.PinHead());
        public IDisposable PinAll() => inner.PinAll();
        public void Dispose() => inner.Dispose();

        private IPbtGroupRecordBufferPin Wrap(IPbtGroupRecordBufferPin pin)
        {
            Interlocked.Increment(ref OutstandingPins);
            return new ForwardedPin(this, pin);
        }

        private sealed class ForwardedWriteBatch(ForwardingGroupRecordBuffer owner, IPbtGroupRecordWriteBatch innerBatch) : IPbtGroupRecordWriteBatch
        {
            public ulong BasePointer => innerBatch.BasePointer;
            public Span<byte> Bytes => innerBatch.Bytes;

            public void Dispose()
            {
                innerBatch.Dispose();
                Interlocked.Increment(ref owner.CompletedWrites);
            }
        }

        private sealed class ForwardedPin(ForwardingGroupRecordBuffer owner, IPbtGroupRecordBufferPin innerPin) : IPbtGroupRecordBufferPin
        {
            private int _disposed;
            public IPbtGroupRecordBuffer Buffer => owner;
            public ulong WriteBoundary => innerPin.WriteBoundary;
            public IPbtGroupRecordBufferPin Copy() => owner.Wrap(innerPin.Copy());

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                innerPin.Dispose();
                Interlocked.Decrement(ref owner.OutstandingPins);
            }
        }
    }
}
