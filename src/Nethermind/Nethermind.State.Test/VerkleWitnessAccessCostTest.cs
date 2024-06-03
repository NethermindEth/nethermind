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
using Nethermind.Int256;
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

        long expectedGas = GasCostOf.WitnessChunkRead * 3
                           + GasCostOf.WitnessBranchRead * 2
                           + GasCostOf.WitnessChunkWrite * 1
                           + GasCostOf.WitnessBranchWrite * 1
                           + GasCostOf.WitnessChunkFill * 1;

        bool result = witness.AccessForTransaction(TestItem.AddressA, TestItem.AddressB, false, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForTransactionWithTransferValueAndDestinationAddress()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 3
                           + GasCostOf.WitnessBranchRead * 2
                           + GasCostOf.WitnessChunkWrite * 2
                           + GasCostOf.WitnessBranchWrite * 2
                           + GasCostOf.WitnessChunkFill * 2;

        bool result = witness.AccessForTransaction(TestItem.AddressA, TestItem.AddressB, true, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForTransactionWithVTransferValue()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 2
                           + GasCostOf.WitnessBranchRead * 1
                           + GasCostOf.WitnessChunkWrite * 1
                           + GasCostOf.WitnessBranchWrite * 1
                           + GasCostOf.WitnessChunkFill * 1;

        bool result = witness.AccessForTransaction(TestItem.AddressA, null, true, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForTransactionWithOneValueIsPresent()
    {
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939502"), VerkleTestUtils.EmptyArray);

        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 2
                           + GasCostOf.WitnessBranchRead * 1
                           + GasCostOf.WitnessChunkWrite * 1
                           + GasCostOf.WitnessBranchWrite * 1
                           + GasCostOf.WitnessChunkFill * 1;

        bool result = witness.AccessForTransaction(TestItem.AddressA, null, false, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForTransactionWithOneValueIsPresentAndDestinationAndTransferValue()
    {
        _verkleTree.Insert(new Hash256("0x3257fdb4ff15d2aac92e0dc30c7a25b45052aea7e1843f992bf450c51c031000"), VerkleTestUtils.EmptyArray);

        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 3
                           + GasCostOf.WitnessBranchRead * 2
                           + GasCostOf.WitnessChunkWrite * 2
                           + GasCostOf.WitnessBranchWrite * 2
                           + GasCostOf.WitnessChunkFill * 1;

        bool result = witness.AccessForTransaction(TestItem.AddressA, TestItem.AddressB, true, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForContractCreationInit()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 1
                           + GasCostOf.WitnessBranchRead * 1
                           + GasCostOf.WitnessChunkWrite * 1
                           + GasCostOf.WitnessBranchWrite * 1
                           + GasCostOf.WitnessChunkFill * 1;

        bool result = witness.AccessForContractCreationInit(TestItem.AddressA, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForContractCreationInitWithAllValueIsPresent()
    {
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939500"), VerkleTestUtils.EmptyArray);

        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 1
                           + GasCostOf.WitnessBranchRead * 1
                           + GasCostOf.WitnessChunkWrite * 1
                           + GasCostOf.WitnessBranchWrite * 1;

        bool result = witness.AccessForContractCreationInit(TestItem.AddressA, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForContractCreationInitWithValueTransfer()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 1
                           + GasCostOf.WitnessBranchRead * 1
                           + GasCostOf.WitnessChunkWrite * 1
                           + GasCostOf.WitnessBranchWrite * 1
                           + GasCostOf.WitnessChunkFill * 1;

        bool result = witness.AccessForContractCreationInit(TestItem.AddressA, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForContractCreationInitWithValueTransferAndWithAllValueIsPresent()
    {
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939500"), VerkleTestUtils.EmptyArray);

        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 1
                           + GasCostOf.WitnessBranchRead * 1
                           + GasCostOf.WitnessChunkWrite * 1
                           + GasCostOf.WitnessBranchWrite * 1;

        bool result = witness.AccessForContractCreationInit(TestItem.AddressA, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForContractCreated()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 2
                           + GasCostOf.WitnessBranchRead * 1
                           + GasCostOf.WitnessChunkWrite * 2
                           + GasCostOf.WitnessBranchWrite * 1
                           + GasCostOf.WitnessChunkFill * 2;

        bool result = witness.AccessForContractCreated(TestItem.AddressA, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForContractCreatedWithAllValueIsPresent()
    {
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939500"), VerkleTestUtils.EmptyArray);
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939501"), VerkleTestUtils.EmptyArray);

        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 2
                           + GasCostOf.WitnessBranchRead * 1
                           + GasCostOf.WitnessChunkWrite * 2
                           + GasCostOf.WitnessBranchWrite * 1;

        bool result = witness.AccessForContractCreated(TestItem.AddressA, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForGasBeneficiary()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 2
                           + GasCostOf.WitnessBranchRead * 1
                           + GasCostOf.WitnessChunkWrite * 1
                           + GasCostOf.WitnessBranchWrite * 1
                           + GasCostOf.WitnessChunkFill * 1;

        bool result = witness.AccessForGasBeneficiary(TestItem.AddressA, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForCodeOpCodes()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 1
                           + GasCostOf.WitnessBranchRead * 1;

        bool result = witness.AccessForCodeOpCodes(TestItem.AddressA, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForBalance()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead + GasCostOf.WitnessBranchRead;

        bool result = witness.AccessForBalanceOpCode(TestItem.AddressA, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForBalanceWithWrite()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead
                           + GasCostOf.WitnessBranchRead
                           + GasCostOf.WitnessChunkWrite
                           + GasCostOf.WitnessBranchWrite
                           + GasCostOf.WitnessChunkFill;

        bool result = witness.AccessForBalanceOpCode(TestItem.AddressA, ref expectedGas, true);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForBalanceWithWriteAndAllValueIsPresent()
    {
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939500"), VerkleTestUtils.EmptyArray);
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead
                           + GasCostOf.WitnessBranchRead
                           + GasCostOf.WitnessChunkWrite
                           + GasCostOf.WitnessBranchWrite;

        bool result = witness.AccessForBalanceOpCode(TestItem.AddressA, ref expectedGas, true);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForCodeHash()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead
                           + GasCostOf.WitnessBranchRead;

        bool result = witness.AccessForCodeHash(TestItem.AddressA, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForStorage()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead
                           + GasCostOf.WitnessBranchRead;

        bool result = witness.AccessForStorage(TestItem.AddressA, UInt256.Zero, false, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForStorageWithWrite()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead
                           + GasCostOf.WitnessBranchRead
                           + GasCostOf.WitnessChunkWrite
                           + GasCostOf.WitnessBranchWrite
                           + GasCostOf.WitnessChunkFill;

        bool result = witness.AccessForStorage(TestItem.AddressA, UInt256.Zero, true, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForStorageWithWriteAndValueIsPresent()
    {
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939540"), VerkleTestUtils.EmptyArray);
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead
                           + GasCostOf.WitnessBranchRead
                           + GasCostOf.WitnessChunkWrite
                           + GasCostOf.WitnessBranchWrite;

        bool result = witness.AccessForStorage(TestItem.AddressA, UInt256.Zero, true, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForCodeProgramCounter()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead
                           + GasCostOf.WitnessBranchRead;

        bool result = witness.AccessForCodeProgramCounter(TestItem.AddressA, 0, false, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForCodeProgramCounterWithWrite()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead
                           + GasCostOf.WitnessBranchRead
                           + GasCostOf.WitnessChunkWrite
                           + GasCostOf.WitnessBranchWrite
                           + GasCostOf.WitnessChunkFill;


        bool result = witness.AccessForCodeProgramCounter(TestItem.AddressA, 0, true, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForCodeProgramCounterWithWriteAndValueIsPresent()
    {
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939580"), VerkleTestUtils.EmptyArray);
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead
                           + GasCostOf.WitnessBranchRead
                           + GasCostOf.WitnessChunkWrite
                           + GasCostOf.WitnessBranchWrite;


        bool result = witness.AccessForCodeProgramCounter(TestItem.AddressA, 0, true, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessCodeChunk()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead
                           + GasCostOf.WitnessBranchRead;

        bool result = witness.AccessCodeChunk(TestItem.AddressA, 0, false, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessCodeChunkWithWrite()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead
                           + GasCostOf.WitnessBranchRead
                           + GasCostOf.WitnessChunkWrite
                           + GasCostOf.WitnessBranchWrite
                           + GasCostOf.WitnessChunkFill;

        bool result = witness.AccessCodeChunk(TestItem.AddressA, 0, true, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessCodeChunkWithWriteAndValueIsPresent()
    {
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939580"), VerkleTestUtils.EmptyArray);
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead
                           + GasCostOf.WitnessBranchRead
                           + GasCostOf.WitnessChunkWrite
                           + GasCostOf.WitnessBranchWrite;

        bool result = witness.AccessCodeChunk(TestItem.AddressA, 0, true, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForAbsentAccount()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 2
                           + GasCostOf.WitnessBranchRead;

        bool result = witness.AccessForAbsentAccount(TestItem.AddressA, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessCompleteAccount()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 2
                           + GasCostOf.WitnessBranchRead;

        bool result = witness.AccessCompleteAccount(TestItem.AddressA, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessCompleteAccountWithWrite()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 2
                           + GasCostOf.WitnessBranchRead
                           + GasCostOf.WitnessChunkWrite * 2
                           + GasCostOf.WitnessBranchWrite
                           + GasCostOf.WitnessChunkFill * 2;

        bool result = witness.AccessCompleteAccount(TestItem.AddressA, ref expectedGas, true);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessCompleteAccountWithWriteAndValueIsPresent()
    {
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939500"), VerkleTestUtils.EmptyArray);
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939502"), VerkleTestUtils.EmptyArray);
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939501"), VerkleTestUtils.EmptyArray);
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939503"), VerkleTestUtils.EmptyArray);
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939504"), VerkleTestUtils.EmptyArray);

        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 2
                           + GasCostOf.WitnessBranchRead
                           + GasCostOf.WitnessChunkWrite * 2
                           + GasCostOf.WitnessBranchWrite;

        bool result = witness.AccessCompleteAccount(TestItem.AddressA, ref expectedGas, true);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForSelfDestruct()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 2
                           + GasCostOf.WitnessBranchRead * 2
                           + GasCostOf.WitnessChunkWrite * 2
                           + GasCostOf.WitnessBranchWrite * 2
                           + GasCostOf.WitnessChunkFill * 2;

        bool result = witness.AccessForSelfDestruct(TestItem.AddressA, TestItem.AddressB, false, false, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForSelfDestructWithBalanceZero()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead
                           + GasCostOf.WitnessBranchRead;

        bool result = witness.AccessForSelfDestruct(TestItem.AddressA, TestItem.AddressB, true, false, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForSelfDestructWithInheritorExist()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 2
                           + GasCostOf.WitnessBranchRead * 2
                           + GasCostOf.WitnessChunkWrite * 2
                           + GasCostOf.WitnessBranchWrite * 2
                           + GasCostOf.WitnessChunkFill * 2;

        bool result = witness.AccessForSelfDestruct(TestItem.AddressA, TestItem.AddressB, false, true, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForSelfDestructWithBalanceZeroAndInheritorExist()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead
                           + GasCostOf.WitnessBranchRead;

        bool result = witness.AccessForSelfDestruct(TestItem.AddressA, TestItem.AddressB, true, true, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForSelfDestructWithEqualAddresses()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 1
                           + GasCostOf.WitnessBranchRead * 1
                           + GasCostOf.WitnessChunkWrite * 1
                           + GasCostOf.WitnessBranchWrite * 1
                           + GasCostOf.WitnessChunkFill * 1;

        bool result = witness.AccessForSelfDestruct(TestItem.AddressA, TestItem.AddressA, false, false, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForSelfDestructWithEqualAddressesAndBalanceZero()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 1
                           + GasCostOf.WitnessBranchRead * 1;

        bool result = witness.AccessForSelfDestruct(TestItem.AddressA, TestItem.AddressA, true, false, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForSelfDestructWithEqualAddressesAndInheritorExist()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 1
                           + GasCostOf.WitnessBranchRead * 1
                           + GasCostOf.WitnessChunkWrite * 1
                           + GasCostOf.WitnessBranchWrite * 1
                           + GasCostOf.WitnessChunkFill * 1;

        bool result = witness.AccessForSelfDestruct(TestItem.AddressA, TestItem.AddressA, false, true, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessForSelfDestructWithEqualAddressesAndBalanceZeroAndInheritorExist()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        long expectedGas = GasCostOf.WitnessChunkRead * 1
                           + GasCostOf.WitnessBranchRead * 1;

        bool result = witness.AccessForSelfDestruct(TestItem.AddressA, TestItem.AddressA, true, true, ref expectedGas);

        Assert.True(result);
        Assert.That(expectedGas, Is.EqualTo(0));
    }

    [Test]
    public void TestAccessAndChargeForCodeSliceWithStartEqualsEnd()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        int startIncluded = 0;
        int endNotIncluded = 0;
        long unspentGas = 0;
        long expectedUnspentGas = 0;

        bool actualResult = witness.AccessAndChargeForCodeSlice(TestItem.AddressA, startIncluded, endNotIncluded, false, ref unspentGas);

        Assert.That(actualResult, Is.True);
        Assert.That(unspentGas, Is.EqualTo(expectedUnspentGas));
    }

    [Test]
    public void TestAccessAndChargeForCodeSliceWithOneIterationAndLessGas()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        int startIncluded = 0;
        int endNotIncluded = 1;
        long unspentGas = 0;
        long expectedUnspentGas = 0;

        bool actualResult = witness.AccessAndChargeForCodeSlice(TestItem.AddressA, startIncluded, endNotIncluded, false, ref unspentGas);

        Assert.That(actualResult, Is.False);
        Assert.That(unspentGas, Is.EqualTo(expectedUnspentGas));
    }

    [Test]
    public void TestAccessAndChargeForCodeSliceWith1Iteration()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        int startIncluded = 0;
        int endNotIncluded = 1;
        long unspentGas = GasCostOf.WitnessChunkRead + GasCostOf.WitnessBranchRead;
        long expectedUnspentGas = 0;

        bool actualResult = witness.AccessAndChargeForCodeSlice(TestItem.AddressA, startIncluded, endNotIncluded, false, ref unspentGas);

        Assert.That(actualResult, Is.True);
        Assert.That(unspentGas, Is.EqualTo(expectedUnspentGas));
    }

    [Test]
    public void TestAccessAndChargeForCodeSliceWith2Iterations()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        int startIncluded = 0;
        int endNotIncluded = 32;
        long unspentGas = GasCostOf.WitnessChunkRead * 2 + GasCostOf.WitnessBranchRead;
        long expectedUnspentGas = 0;

        bool actualResult = witness.AccessAndChargeForCodeSlice(TestItem.AddressA, startIncluded, endNotIncluded, false, ref unspentGas);

        Assert.That(actualResult, Is.True);
        Assert.That(unspentGas, Is.EqualTo(expectedUnspentGas));
    }

    [Test]
    public void TestAccessAndChargeForCodeSliceWithWrite()
    {
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        int startIncluded = 0;
        int endNotIncluded = 1;
        long unspentGas = GasCostOf.WitnessChunkRead
                          + GasCostOf.WitnessBranchRead
                          + GasCostOf.WitnessChunkWrite
                          + GasCostOf.WitnessBranchWrite
                          + GasCostOf.WitnessChunkFill;
        long expectedUnspentGas = 0;

        bool actualResult = witness.AccessAndChargeForCodeSlice(TestItem.AddressA, startIncluded, endNotIncluded, true, ref unspentGas);

        Assert.That(actualResult, Is.True);
        Assert.That(unspentGas, Is.EqualTo(expectedUnspentGas));
    }

    [Test]
    public void TestAccessAndChargeForCodeSliceWithWriteAndValueIsPresent()
    {
        _verkleTree.Insert(new Hash256("0x701a7bfd49d69fa6f316ea3f6b694a263bd4fa07a00e0e30e0891536ad939580"), VerkleTestUtils.EmptyArray);
        var witness = new VerkleExecWitness(NullLogManager.Instance, _verkleWorldState);

        int startIncluded = 0;
        int endNotIncluded = 1;
        long unspentGas = GasCostOf.WitnessChunkRead
                          + GasCostOf.WitnessBranchRead
                          + GasCostOf.WitnessChunkWrite
                          + GasCostOf.WitnessBranchWrite;

        long expectedUnspentGas = 0;

        bool actualResult = witness.AccessAndChargeForCodeSlice(TestItem.AddressA, startIncluded, endNotIncluded, true, ref unspentGas);

        Assert.That(actualResult, Is.True);
        Assert.That(unspentGas, Is.EqualTo(expectedUnspentGas));
    }

}
