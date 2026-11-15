// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

public class GuestRevertReasonTests
{
    [Test]
    public void Revert_reason_is_not_decoded_with_a_tracer_connected()
    {
        byte[] output = new byte[4 + 32 + 32 + 32];
        output[0] = 0x08;
        output[1] = 0xc3;
        output[2] = 0x79;
        output[3] = 0xa0;
        output[4 + 31] = 0x20;
        output[4 + 32 + 31] = 0x02;
        output[4 + 64] = (byte)'n';
        output[4 + 65] = (byte)'o';

        TransactionSubstate substate = new(new ReadOnlyMemory<byte>(output), 0, null, null, shouldRevert: true, isTracerConnected: true);

        Assert.That(substate.Error, Is.EqualTo(TransactionSubstate.Revert));
    }
}
