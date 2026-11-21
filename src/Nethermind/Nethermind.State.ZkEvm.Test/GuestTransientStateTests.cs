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

    [Test]
    public void ReadTransient_reads_as_GetTransientState_does()
    {
        GuestWorldState state = new(GuestStorageOriginalsTests.HashFreeScopeProvider());
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        IGuestSlotStore slots = state;

        slots.ReadTransient(Contract, 5, out UInt256 beforeAnyWrite);
        state.SetTransientState(new StorageCell(Contract, 5), 7);
        slots.ReadTransient(Contract, 5, out UInt256 written);
        slots.ReadTransient(Contract, 6, out UInt256 other);
        state.ResetTransient();
        slots.ReadTransient(Contract, 5, out UInt256 afterReset);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeAnyWrite, Is.EqualTo(UInt256.Zero));
            Assert.That(written, Is.EqualTo((UInt256)7));
            Assert.That(other, Is.EqualTo(UInt256.Zero));
            Assert.That(afterReset, Is.EqualTo(UInt256.Zero));
        }
    }
}
