// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.Rocks;
using Nethermind.Evm;
using Nethermind.Evm.Witness;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Verkle.Tree.TreeStore;
using NUnit.Framework;
using Nethermind.Db;
using Nethermind.Verkle.Tree.Test;

namespace Nethermind.Store.Test;

public class VerkleWitnessAccessCostTest
{
    private VerkleWorldState _verkleWorldState;
    private VerkleStateTree _verkleTree;

    [SetUp]
    public void Setup()
    {
        IDbProvider provider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
        var store = new VerkleTreeStore<VerkleSyncCache>(provider, LimboLogs.Instance);
        IDb codeDb = provider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);

        _verkleTree = new VerkleStateTree(store, LimboLogs.Instance);
        _verkleWorldState = new VerkleWorldState(_verkleTree, codeDb, LimboLogs.Instance);
    }

    [Test]
    public void TestAccessForTransactionWithDestinationAddress()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 10
                           + GasCostOf.WitnessBranchRead * 2
                           + GasCostOf.WitnessChunkWrite * 2
                           + GasCostOf.WitnessBranchWrite * 1
                           + GasCostOf.WitnessChunkFill * 2;

        long actualGas = witness.AccessForTransaction(TestItem.AddressA, TestItem.AddressB, false);

        Assert.That(actualGas, Is.EqualTo(expectedGas));
    }

    [Test]
    public void TestAccessForTransactionWithValueAndDestinationAddress()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 10
                           + GasCostOf.WitnessBranchRead * 2
                           + GasCostOf.WitnessChunkWrite * 3
                           + GasCostOf.WitnessBranchWrite * 2
                           + GasCostOf.WitnessChunkFill * 3;

        long actualGas = witness.AccessForTransaction(TestItem.AddressA, TestItem.AddressB, true);

        Assert.That(actualGas, Is.EqualTo(expectedGas));
    }

    [Test]
    public void TestAccessForTransactionWithValue()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 5
                           + GasCostOf.WitnessBranchRead * 1
                           + GasCostOf.WitnessChunkWrite * 2
                           + GasCostOf.WitnessBranchWrite * 1
                           + GasCostOf.WitnessChunkFill * 2;

        long actualGas = witness.AccessForTransaction(TestItem.AddressA, null, true);

        Assert.That(actualGas, Is.EqualTo(expectedGas));
    }

    [Test]
    public void TestAccessForTransactionValueIsPresent()
    {
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939502"), VerkleTestUtils.EmptyArray);
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939501"), VerkleTestUtils.EmptyArray);

        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 5
                           + GasCostOf.WitnessBranchRead * 1
                           + GasCostOf.WitnessChunkWrite * 2
                           + GasCostOf.WitnessBranchWrite * 1;

        long actualGas = witness.AccessForTransaction(TestItem.AddressA, null, false);

        Assert.That(actualGas, Is.EqualTo(expectedGas));
    }
}
