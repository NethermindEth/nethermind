// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class BeaconBlockRootHandlerTests
{
    private BeaconBlockRootHandler _beaconBlockRootHandler;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _worldState;

    [SetUp]
    public void Setup()
    {
        _worldState = Substitute.For<IWorldState>();
        _transactionProcessor = Substitute.For<ITransactionProcessor>();
        _beaconBlockRootHandler = new BeaconBlockRootHandler(_transactionProcessor, _worldState);
    }

    [Test]
    public void Test_BeaconRootsAccessList_IsBeaconBlockRootAvailableFalse()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithParentBeaconBlockRoot(Hash256.Zero).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        _worldState.AccountExists(Arg.Any<Address>()).Returns(true);

        (Address? toAddress, AccessList? accessList) = _beaconBlockRootHandler
            .BeaconRootsAccessList(block, Shanghai.Instance);

        Assert.That(accessList, Is.Null);
        Assert.That(toAddress, Is.Null);
    }

    [Test]
    public void Test_BeaconRootsAccessList_HeaderIsGenesis()
    {
        BlockHeader header = Build.A.BlockHeader.WithParentBeaconBlockRoot(Hash256.Zero).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        _worldState.AccountExists(Arg.Any<Address>()).Returns(true);

        (Address? toAddress, AccessList? accessList) = _beaconBlockRootHandler
            .BeaconRootsAccessList(block, Cancun.Instance);

        Assert.That(accessList, Is.Null);
        Assert.That(toAddress, Is.Null);
    }

    [Test]
    public void Test_BeaconRootsAccessList_ParentBeaconBlockRootIsNull()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        _worldState.AccountExists(Arg.Any<Address>()).Returns(true);

        (Address? toAddress, AccessList? accessList) = _beaconBlockRootHandler
            .BeaconRootsAccessList(block, Cancun.Instance);

        Assert.That(accessList, Is.Null);
        Assert.That(toAddress, Is.Null);
    }

    [Test]
    public void Test_BeaconRootsAccessList_canInsertBeaconRootIsTrue_AccountNotExist()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithParentBeaconBlockRoot(Hash256.Zero).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        _worldState.AccountExists(Arg.Any<Address>()).Returns(false);

        (Address? toAddress, AccessList? accessList) = _beaconBlockRootHandler
            .BeaconRootsAccessList(block, Cancun.Instance);

        Assert.That(accessList, Is.Null);
        Assert.That(toAddress, Is.Null);
    }

    [Test]
    public void Test_BeaconRootsAccessList_canInsertBeaconRootIsTrue_AccountExists()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithParentBeaconBlockRoot(Hash256.Zero).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        _worldState.AccountExists(Arg.Any<Address>()).Returns(true);
        (_, AccessList? accessList) = _beaconBlockRootHandler
            .BeaconRootsAccessList(block, Cancun.Instance, includeStorageCells: true);

        Assert.That(accessList, Is.Not.Null);
        Assert.That(accessList.Count.AddressesCount, Is.EqualTo(1));
        Assert.That(accessList.Count.StorageKeysCount, Is.EqualTo(2));
    }

    [Test]
    public void Test_BeaconRootsAccessList_canInsertBeaconRootIsTrue_AccountExists_IncludeStorageCellsIsFalse()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithParentBeaconBlockRoot(Hash256.Zero).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        _worldState.AccountExists(Arg.Any<Address>()).Returns(true);
        (_, AccessList? accessList) = _beaconBlockRootHandler
            .BeaconRootsAccessList(block, Cancun.Instance, false);

        Assert.That(accessList, Is.Not.Null);
        Assert.That(accessList.Count.AddressesCount, Is.EqualTo(1));
        Assert.That(accessList.Count.StorageKeysCount, Is.EqualTo(0));
    }

    [Test]
    public void Test_StoreBeaconRoot_AccessListIsNull()
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        var blkCtx = new BlockExecutionContext(header, Cancun.Instance);

        _beaconBlockRootHandler.StoreBeaconRoot(block, in blkCtx, Cancun.Instance, NullTxTracer.Instance);

        _transactionProcessor.DidNotReceive().Execute(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>());
    }

    [Test]
    public void Test_StoreBeaconRoot_AccessListNotNull()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithParentBeaconBlockRoot(Hash256.Zero).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        var blkCtx = new BlockExecutionContext(header, Cancun.Instance);

        _worldState.AccountExists(Arg.Any<Address>()).Returns(true);

        _beaconBlockRootHandler.StoreBeaconRoot(block, in blkCtx, Cancun.Instance, NullTxTracer.Instance);

        Transaction transaction = new()
        {
            Value = UInt256.Zero,
            Data = header.ParentBeaconBlockRoot!.Bytes.ToArray(),
            To = Eip4788Constants.BeaconRootsAddress,
            SenderAddress = Address.SystemUser,
            GasLimit = 30_000_000L,
            GasPrice = UInt256.Zero,
            AccessList = new AccessList.Builder().AddAddress(Eip4788Constants.BeaconRootsAddress).Build()
        };

        transaction.Hash = transaction.CalculateHash();
        _transactionProcessor.Received().Execute(Arg.Is<Transaction>(t =>
            t.Hash == transaction.Hash), new BlockExecutionContext(header, Cancun.Instance), NullTxTracer.Instance);
    }
}
