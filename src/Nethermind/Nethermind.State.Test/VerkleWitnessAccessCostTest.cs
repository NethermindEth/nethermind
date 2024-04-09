// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Witness;
using Nethermind.Logging;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.Utils;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class VerkleWitnessAccessCostTest
{

    [Test]
    public void TestAccessForTransaction()
    {
        var calculator = new VerkleExecWitness(NullLogManager.Instance);
        long gas = calculator.AccessForTransaction(TestItem.AddressA, TestItem.AddressB, false);
        Console.WriteLine(gas);
    }

    [Test]
    public void TestAccessForTransactionWithValue()
    {
        VerkleExecWitness calculator = new(NUnitLogManager.Instance);
        long gas = calculator.AccessForTransaction(TestItem.AddressA, TestItem.AddressB, true);
        Console.WriteLine(gas);
    }
}
