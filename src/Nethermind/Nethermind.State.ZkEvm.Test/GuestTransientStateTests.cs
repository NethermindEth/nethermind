// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.State.ZkEvm.Test;

public class GuestTransientStateTests
{
    private static readonly Address Contract = new("0x00000000000000000000000000000000000000c0");

    public enum Reader { Guest, Traced, TracedParallel }

    [Test]
    public void ReadTransient_reads_as_GetTransientState_does([Values] Reader reader)
    {
        GuestWorldState guest = new(GuestStorageOriginalsTests.HashFreeScopeProvider());
        IWorldState state = reader == Reader.Guest ? guest : new TracedAccessWorldState(guest, reader == Reader.TracedParallel);
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        IGuestSlotStore slots = (IGuestSlotStore)state;

        slots.ReadTransient(Contract, 5, out UInt256 beforeAnyWrite);
        state.SetTransientState(new StorageCell(Contract, 5), 7);
        slots.ReadTransient(Contract, 5, out UInt256 written);
        slots.ReadTransient(Contract, 6, out UInt256 other);
        // TLOAD passes one stack word as both the index and the value.
        UInt256 word = 5;
        slots.ReadTransient(Contract, in word, out word);
        state.ResetTransient();
        slots.ReadTransient(Contract, 5, out UInt256 afterReset);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeAnyWrite, Is.EqualTo(UInt256.Zero));
            Assert.That(written, Is.EqualTo((UInt256)7));
            Assert.That(other, Is.EqualTo(UInt256.Zero));
            Assert.That(word, Is.EqualTo((UInt256)7));
            Assert.That(afterReset, Is.EqualTo(UInt256.Zero));
        }
    }
}
