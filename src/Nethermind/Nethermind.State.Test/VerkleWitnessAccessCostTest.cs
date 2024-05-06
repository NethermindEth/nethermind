// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Witness;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Verkle.Tree.TreeStore;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class VerkleWitnessAccessCostTest
{
    private static readonly IVerkleTreeStore _verkleTreeStore = new ReadOnlyNullVerkleTreeStore();
    private static readonly VerkleStateTree _verkleTree = new(_verkleTreeStore, NullLogManager.Instance);
    private static readonly VerklePersistentStorageProvider _verklePersistentStorageProvider = new(_verkleTree, NullLogManager.Instance);
    private static readonly VerkleWorldState _verkleWorldState = new(_verklePersistentStorageProvider, _verkleTree, null, NullLogManager.Instance);

    [Test]
    public void TestAccessForTransaction()
    {
        var calculator = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);
        long gas = calculator.AccessForTransaction(TestItem.AddressA, TestItem.AddressB, false);
        Console.WriteLine(gas);
    }

    [Test]
    public void TestAccessForTransactionWithValue()
    {
        VerkleExecWitness calculator = new(NUnitLogManager.Instance, _verkleWorldState);
        long gas = calculator.AccessForTransaction(TestItem.AddressA, TestItem.AddressB, true);
        Console.WriteLine(gas);
    }
}
