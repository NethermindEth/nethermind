// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Validates that the hand-crafted EVM bytecodes used by <c>BlockProcessingBenchmark</c>
/// execute correctly and produce the expected storage mutations and log events.
/// </summary>
[TestFixture]
public class BlockProcessingBenchmarkValidationTests
{
    private static readonly IReleaseSpec Spec = Osaka.Instance;

    private static readonly Address Erc20Address = Address.FromNumber(0x1000);
    private static readonly Address SwapAddress = Address.FromNumber(0x2000);

    // Minimal bytecode (STOP) for system contract stubs
    private static readonly byte[] StopCode = [0x00];

    private readonly PrivateKey _senderKey = TestItem.PrivateKeyA;
    private Address _sender = null!;

    private IContainer _container = null!;
    private ILifetimeScope _processingScope = null!;
    private IBranchProcessor _branchProcessor = null!;
    private IWorldState _worldState = null!;
    private BlockHeader _parentHeader = null!;
    private BlockHeader _header = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _sender = _senderKey.Address;

        _header = Build.A.BlockHeader
            .WithNumber(1)
            .WithGasLimit(30_000_000)
            .WithBaseFee(1.GWei())
            .WithTimestamp(1000)
            .TestObject;

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Osaka.Instance))
            .Build();

        IDbProvider dbProvider = TestMemDbProvider.Init();
        IWorldStateManager wsm = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, LimboLogs.Instance);
        IWorldStateScopeProvider scopeProvider = wsm.GlobalWorldState;

        IBlockValidationModule[] validationModules = _container.Resolve<IBlockValidationModule[]>();
        IMainProcessingModule[] mainProcessingModules = _container.Resolve<IMainProcessingModule[]>();
        _processingScope = _container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned();
            b.RegisterInstance(wsm).As<IWorldStateManager>().ExternallyOwned();
            b.AddModule(validationModules);
            b.AddModule(mainProcessingModules);
        });

        _worldState = _processingScope.Resolve<IWorldState>();

        using (_worldState.BeginScope(IWorldState.PreGenesis))
        {
            _worldState.CreateAccount(_sender, 1_000_000.Ether());
            _worldState.CreateAccount(TestItem.AddressB, UInt256.Zero);

            _worldState.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, UInt256.Zero);
            _worldState.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, StopCode, Spec);
            _worldState.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, UInt256.Zero);
            _worldState.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, StopCode, Spec);

            // ── ERC20 contract ──
            _worldState.CreateAccount(Erc20Address, UInt256.Zero);
            _worldState.InsertCode(Erc20Address, StorageBenchmarkContracts.BuildErc20RuntimeCode(), Spec);

            UInt256 senderBalanceSlot = StorageBenchmarkContracts.ComputeMappingSlot(_sender, UInt256.Zero);
            byte[] senderBalance = new byte[32];
            ((UInt256)1_000_000).ToBigEndian(senderBalance);
            _worldState.Set(new StorageCell(Erc20Address, senderBalanceSlot), senderBalance);

            // ── Swap contract ──
            _worldState.CreateAccount(SwapAddress, UInt256.Zero);
            _worldState.InsertCode(SwapAddress, StorageBenchmarkContracts.BuildSwapRuntimeCode(), Spec);

            SeedSlot(SwapAddress, 0, 1_000_000_000);    // reserve0
            SeedSlot(SwapAddress, 1, 1_000_000_000);    // reserve1
            SeedSlot(SwapAddress, 2, 500_000);           // totalLiquidity
            SeedSlot(SwapAddress, 3, 30);                // feeAccumulator
            SeedSlot(SwapAddress, 4, 1);                 // lastTimestamp
            SeedSlot(SwapAddress, 5, 1);                 // priceCumulative0
            SeedSlot(SwapAddress, 6, 1);                 // priceCumulative1
            SeedSlot(SwapAddress, 7, 1_000_000_000);    // kLast

            _worldState.Commit(Spec);
            _worldState.CommitTree(0);

            _parentHeader = Build.A.BlockHeader
                .WithNumber(0)
                .WithStateRoot(_worldState.StateRoot)
                .WithGasLimit(30_000_000)
                .TestObject;
        }

        _branchProcessor = _processingScope.Resolve<IBranchProcessor>();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _processingScope.Dispose();
        _container.Dispose();
    }

    [Test]
    public void ERC20_Transfer_Executes_Correctly()
    {
        Address recipient = Address.FromNumber(0x999);

        // Build calldata: [to (32 bytes), amount (32 bytes)]
        byte[] calldata = new byte[64];
        recipient.Bytes.CopyTo(calldata.AsSpan(12));
        ((UInt256)1).ToBigEndian(calldata.AsSpan(32));

        Transaction tx = Build.A.Transaction
            .WithNonce(0)
            .WithTo(Erc20Address)
            .WithData(calldata)
            .WithGasLimit(100_000)
            .WithGasPrice(2.GWei())
            .SignedAndResolved(_senderKey)
            .TestObject;

        Block block = Build.A.Block
            .WithHeader(_header)
            .WithTransactions(tx)
            .TestObject;

        // Process with receipt tracing
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);

        Block[] processed = _branchProcessor.Process(
            _parentHeader, [block], ProcessingOptions.NoValidation, receiptsTracer);

        // Verify receipt
        TxReceipt receipt = receiptsTracer.TxReceipts[0];
        Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success), $"Tx reverted: {receipt.Error}");
        Assert.That(receipt.GasUsed, Is.GreaterThan(21_000), "EVM code was not executed (only intrinsic gas)");
        Assert.That(receipt.Logs, Is.Not.Null);
        Assert.That(receipt.Logs!.Length, Is.EqualTo(1), "Expected 1 Transfer event");
        Assert.That(receipt.Logs[0].Topics.Length, Is.EqualTo(3), "LOG3 should have 3 topics");

        // Verify storage changes
        using (_worldState.BeginScope(processed[0].Header))
        {
            UInt256 senderSlot = StorageBenchmarkContracts.ComputeMappingSlot(_sender, UInt256.Zero);
            UInt256 recipientSlot = StorageBenchmarkContracts.ComputeMappingSlot(recipient, UInt256.Zero);

            ReadOnlySpan<byte> senderBal = _worldState.Get(new StorageCell(Erc20Address, senderSlot));
            ReadOnlySpan<byte> recipientBal = _worldState.Get(new StorageCell(Erc20Address, recipientSlot));

            UInt256 senderValue = senderBal.IsEmpty ? UInt256.Zero : new UInt256(senderBal, isBigEndian: true);
            UInt256 recipientValue = recipientBal.IsEmpty ? UInt256.Zero : new UInt256(recipientBal, isBigEndian: true);

            Assert.That(senderValue, Is.EqualTo((UInt256)999_999), "Sender balance should decrease by 1");
            Assert.That(recipientValue, Is.EqualTo((UInt256)1), "Recipient balance should be 1");
        }
    }

    [Test]
    public void Swap_Executes_Correctly()
    {
        UInt256 amountIn = 100;

        // Build calldata: [amountIn (32 bytes)]
        byte[] calldata = new byte[32];
        amountIn.ToBigEndian(calldata);

        Transaction tx = Build.A.Transaction
            .WithNonce(0)
            .WithTo(SwapAddress)
            .WithData(calldata)
            .WithGasLimit(200_000)
            .WithGasPrice(2.GWei())
            .SignedAndResolved(_senderKey)
            .TestObject;

        Block block = Build.A.Block
            .WithHeader(_header)
            .WithTransactions(tx)
            .TestObject;

        // Process with receipt tracing
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);

        Block[] processed = _branchProcessor.Process(
            _parentHeader, [block], ProcessingOptions.NoValidation, receiptsTracer);

        // Verify receipt
        TxReceipt receipt = receiptsTracer.TxReceipts[0];
        Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success), $"Tx reverted: {receipt.Error}");
        Assert.That(receipt.GasUsed, Is.GreaterThan(21_000), "EVM code was not executed (only intrinsic gas)");
        Assert.That(receipt.Logs, Is.Not.Null);
        Assert.That(receipt.Logs!.Length, Is.EqualTo(1), "Expected 1 Swap event");

        // Verify storage changes
        using (_worldState.BeginScope(processed[0].Header))
        {
            UInt256 reserve0 = ReadStorageUInt256(SwapAddress, 0);
            UInt256 reserve1 = ReadStorageUInt256(SwapAddress, 1);
            UInt256 feeAcc = ReadStorageUInt256(SwapAddress, 3);
            UInt256 lastTs = ReadStorageUInt256(SwapAddress, 4);
            UInt256 priceCum0 = ReadStorageUInt256(SwapAddress, 5);

            // reserve0 += 100
            Assert.That(reserve0, Is.EqualTo((UInt256)(1_000_000_000 + 100)),
                "reserve0 should increase by amountIn");
            // reserve1 -= 100/2 = 50
            Assert.That(reserve1, Is.EqualTo((UInt256)(1_000_000_000 - 50)),
                "reserve1 should decrease by amountIn/2");
            // feeAccumulator += 100*3/1000 = 0 (integer division: 300/1000=0)
            // Initial was 30, so still 30
            Assert.That(feeAcc, Is.EqualTo((UInt256)30),
                "feeAccumulator: 100*3/1000=0, no change expected");
            // lastTimestamp = block.timestamp (1000 from header)
            Assert.That(lastTs, Is.EqualTo((UInt256)1000),
                "lastTimestamp should be block timestamp");
            // priceCumulative0 += 1
            Assert.That(priceCum0, Is.EqualTo((UInt256)2),
                "priceCumulative0 should increment by 1");

            // Sender balance mapping: += amountIn/2 = 50
            UInt256 senderSlot = StorageBenchmarkContracts.ComputeMappingSlot(_sender, (UInt256)8);
            UInt256 senderBal = ReadStorageUInt256(SwapAddress, senderSlot);
            Assert.That(senderBal, Is.EqualTo((UInt256)50),
                "Sender mapping balance should be amountIn/2");
        }
    }

    private UInt256 ReadStorageUInt256(Address address, UInt256 slot)
    {
        ReadOnlySpan<byte> value = _worldState.Get(new StorageCell(address, slot));
        return value.IsEmpty ? UInt256.Zero : new UInt256(value, isBigEndian: true);
    }

    private void SeedSlot(Address address, UInt256 slot, UInt256 value)
    {
        byte[] bytes = new byte[32];
        value.ToBigEndian(bytes);
        _worldState.Set(new StorageCell(address, slot), bytes);
    }

    private Block BuildBlock(params Transaction[] transactions)
        => Build.A.Block
            .WithHeader(_header)
            .WithTransactions(transactions)
            .TestObject;
}
