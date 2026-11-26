// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

public class GuestEip8279Tests
{
    [Test]
    public void Authorization_bytes_fold_to_zero_outside_the_guest_fork_range()
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8279Enabled.Returns(true);
        Transaction transaction = new()
        {
            Type = TxType.SetCode,
            AuthorizationList = [new AuthorizationTuple(1, Address.Zero, 0, new Signature(new byte[64], 0))],
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SpecFlags.ConstEip8279, Is.False);
            Assert.That(IntrinsicGasCalculator.CalculateAuthorizationBalBytes(transaction, spec), Is.Zero);
        }
    }
}
