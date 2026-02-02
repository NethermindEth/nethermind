// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Test;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[TestFixture]
public class BlockAccessListTests() : VirtualMachineTestsBase
{
    private static readonly IReleaseSpec _spec = Amsterdam.Instance;

    private static readonly ISpecProvider _specProvider = new TestSpecProvider(_spec);
    private static readonly EthereumEcdsa _ecdsa = new(0);
    private static readonly UInt256 _accountBalance = 10.Ether();
    private static readonly long _gasLimit = 100000;
    private static readonly Address _testAddress = ContractAddress.From(TestItem.AddressA, 0);

    // do e2e with engine api?
    [Test]
    public async Task Constructs_BAL_when_processing_block()
    {
        using BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create(BuildContainer());

        IWorldState mainWorldState = testBlockchain.MainWorldState;
        ParallelWorldState? tracedWorldState = mainWorldState as ParallelWorldState;
        Assert.That(tracedWorldState, Is.Not.Null, "Main world state should be ParallelWorldState");

        // Begin scope and initialize state
        using IDisposable _ = mainWorldState.BeginScope(IWorldState.PreGenesis);
        InitWorldState(mainWorldState);

        tracedWorldState!.GeneratedBlockAccessList = new();

        const long gasUsed = 167340;
        const long gasUsedBeforeFinal = 92100;
        const ulong gasPrice = 2;
        const long gasLimit = 100000;
        const ulong timestamp = 1000000;
        Hash256 parentHash = new("0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c");

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithSenderAddress(TestItem.AddressA)
            .WithValue(0)
            .WithGasPrice(gasPrice)
            .WithGasLimit(gasLimit)
            .TestObject;

        Transaction tx2 = Build.A.Transaction
            .WithTo(null)
            .WithSenderAddress(TestItem.AddressA)
            .WithValue(0)
            .WithNonce(1)
            .WithGasPrice(gasPrice)
            .WithGasLimit(gasLimit)
            .WithCode(Eip2935TestConstants.InitCode)
            .TestObject;

        /*
        Store followed by revert should undo storage change
        PUSH1 1
        PUSH1 1
        SSTORE
        PUSH0
        PUSH0
        REVERT
        */
        byte[] code = Bytes.FromHexString("0x60016001555f5ffd");
        Transaction tx3 = Build.A.Transaction
            .WithTo(null)
            .WithSenderAddress(TestItem.AddressA)
            .WithValue(0)
            .WithNonce(2)
            .WithGasPrice(gasPrice)
            .WithGasLimit(gasLimit)
            .WithCode(code)
            .TestObject;

        BlockHeader header = Build.A.BlockHeader
            .WithBaseFee(1)
            .WithNumber(1)
            .WithGasUsed(gasUsed)
            .WithReceiptsRoot(new("0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569"))
            .WithStateRoot(new("0x9399acd9f2603778c11646f05f7827509b5319815da74b5721a07defb6285c8d"))
            .WithBlobGasUsed(0)
            .WithBeneficiary(TestItem.AddressC)
            .WithParentBeaconBlockRoot(Hash256.Zero)
            .WithRequestsHash(new("0xe3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"))
            .WithBlockAccessListHash(new("0xa19f3798cdc08ff0bdee830bb5daf6954ecbd8723c810285fef3240d06d2bf18"))
            .WithTimestamp(timestamp)
            .WithParentHash(parentHash)
            .TestObject;

        Withdrawal withdrawal = new()
        {
            Index = 0,
            ValidatorIndex = 0,
            Address = TestItem.AddressD,
            AmountInGwei = 1
        };

        Block block = Build.A.Block
            .WithTransactions([tx, tx2, tx3])
            .WithBaseFeePerGas(1)
            .WithWithdrawals([withdrawal])
            .WithHeader(header).TestObject;

        (Block processedBlock, TxReceipt[] _) = testBlockchain.BlockProcessor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, _spec, CancellationToken.None);

        BlockAccessList blockAccessList = processedBlock.GeneratedBlockAccessList;
        Assert.That(blockAccessList.AccountChanges.Count, Is.EqualTo(10));

        Address newContractAddress = ContractAddress.From(TestItem.AddressA, 1);
        Address newContractAddress2 = ContractAddress.From(TestItem.AddressA, 2);

        AccountChanges addressAChanges = blockAccessList.GetAccountChanges(TestItem.AddressA)!;
        AccountChanges addressBChanges = blockAccessList.GetAccountChanges(TestItem.AddressB)!;
        AccountChanges addressCChanges = blockAccessList.GetAccountChanges(TestItem.AddressC)!;
        AccountChanges addressDChanges = blockAccessList.GetAccountChanges(TestItem.AddressD)!;
        AccountChanges newContractChanges = blockAccessList.GetAccountChanges(newContractAddress)!;
        AccountChanges newContractChanges2 = blockAccessList.GetAccountChanges(newContractAddress2)!;
        AccountChanges eip2935Changes = blockAccessList.GetAccountChanges(Eip2935Constants.BlockHashHistoryAddress)!;
        AccountChanges eip4788Changes = blockAccessList.GetAccountChanges(Eip4788Constants.BeaconRootsAddress)!;
        AccountChanges eip7002Changes = blockAccessList.GetAccountChanges(Eip7002Constants.WithdrawalRequestPredeployAddress)!;
        AccountChanges eip7251Changes = blockAccessList.GetAccountChanges(Eip7251Constants.ConsolidationRequestPredeployAddress)!;

        UInt256 eip4788Slot1 = timestamp % Eip4788Constants.RingBufferSize;
        UInt256 eip4788Slot2 = (timestamp % Eip4788Constants.RingBufferSize) + Eip4788Constants.RingBufferSize;

        StorageChange parentHashStorageChange = new(0, new UInt256(parentHash.BytesToArray(), isBigEndian: true));
        StorageChange calldataStorageChange = new(0, 0);
        StorageChange timestampStorageChange = new(0, 0xF4240);
        StorageChange zeroStorageChangeEnd = new(3, 0);

        UInt256 addressABalance = _accountBalance - gasPrice * GasCostOf.Transaction;
        UInt256 addressABalance2 = _accountBalance - gasPrice * gasUsedBeforeFinal;
        UInt256 addressABalance3 = _accountBalance - gasPrice * gasUsed;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(addressAChanges, Is.EqualTo(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges([new(1, addressABalance), new(2, addressABalance2), new(3, addressABalance3)])
                    .WithNonceChanges([new(1, 1), new(2, 2), new(3, 3)])
                    .TestObject));

            Assert.That(addressBChanges, Is.EqualTo(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressB)
                    .TestObject));

            Assert.That(addressCChanges, Is.EqualTo(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressC)
                    .WithBalanceChanges([new(1, new UInt256(GasCostOf.Transaction)), new(2, new UInt256(gasUsedBeforeFinal)), new(3, new UInt256(gasUsed))])
                    .TestObject));

            Assert.That(addressDChanges, Is.EqualTo(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressD)
                    .WithBalanceChanges([new(4, 1.GWei())])
                    .TestObject));

            Assert.That(newContractChanges, Is.EqualTo(
                Build.An.AccountChanges
                    .WithAddress(newContractAddress)
                    .WithNonceChanges([new(2, 1)])
                    .WithCodeChanges([new(2, Eip2935TestConstants.Code)])
                    .TestObject));

            Assert.That(newContractChanges2, Is.EqualTo(
                Build.An.AccountChanges
                    .WithAddress(newContractAddress2)
                    .WithStorageReads(1)
                    .TestObject));

            Assert.That(eip2935Changes, Is.EqualTo(
                Build.An.AccountChanges
                    .WithAddress(Eip2935Constants.BlockHashHistoryAddress)
                    .WithStorageChanges(0, parentHashStorageChange)
                    .TestObject));

            // eip4788 stores timestamp at slot1 and beacon root (0) at slot2
            // beacon root 0→0 is not a change, so only slot1 has a storage change
            // slot1 is not a separate read since it's already a change, only slot2 is read
            Assert.That(eip4788Changes, Is.EqualTo(
                Build.An.AccountChanges
                    .WithAddress(Eip4788Constants.BeaconRootsAddress)
                    .WithStorageChanges(eip4788Slot1, timestampStorageChange)
                    .WithStorageReads(eip4788Slot2)
                    .TestObject));

            // storage reads make no changes
            Assert.That(eip7002Changes, Is.EqualTo(
                Build.An.AccountChanges
                    .WithAddress(Eip7002Constants.WithdrawalRequestPredeployAddress)
                    .WithStorageReads(0, 1, 2, 3)
                    .TestObject));

            // storage reads make no changes
            Assert.That(eip7251Changes, Is.EqualTo(
                Build.An.AccountChanges
                    .WithAddress(Eip7251Constants.ConsolidationRequestPredeployAddress)
                    .WithStorageReads(0, 1, 2, 3)
                    .TestObject));
        }
    }

    [TestCaseSource(nameof(CodeTestSource))]
    public async Task Constructs_BAL_when_processing_code(byte[] code, IEnumerable<AccountChanges> expected)
    {
        InitWorldState(TestState);
        ParallelWorldState worldState = TestState as ParallelWorldState;
        worldState.TracingEnabled = true;

        Transaction createTx = Build.A.Transaction.WithCode(code).WithGasLimit(_gasLimit).WithValue(0).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;
        Block block = Build.A.Block.TestObject;

        _processor.SetBlockExecutionContext( new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)));
        CallOutputTracer callOutputTracer = new();
        TransactionResult res = _processor.Execute(createTx, callOutputTracer);
        BlockAccessList bal = worldState.GeneratedBlockAccessList;
        UInt256 gasUsed = new((ulong)callOutputTracer.GasSpent);

        AccountChanges accountChangesA = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges([new(0, _accountBalance - gasUsed)])
            .WithNonceChanges([new(0, 1)]).TestObject;
        AccountChanges accountChangesZero = Build.An.AccountChanges.WithBalanceChanges([new(0, gasUsed)]).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(res.TransactionExecuted);
            Assert.That(bal.GetAccountChanges(TestItem.AddressA), Is.EqualTo(accountChangesA));
            Assert.That(bal.GetAccountChanges(Address.Zero), Is.EqualTo(accountChangesZero));
            Assert.That(bal.AccountChanges.Count(), Is.EqualTo(expected.Count() + 2));
        }

        foreach(AccountChanges expectedAccountChanges in expected)
        {
            Assert.That(bal.GetAccountChanges(expectedAccountChanges.Address), Is.EqualTo(expectedAccountChanges));
        }
    }

    private static Action<ContainerBuilder> BuildContainer()
        => containerBuilder => containerBuilder.AddSingleton(_specProvider);

    private static void InitWorldState(IWorldState worldState)
    {
        worldState.CreateAccount(TestItem.AddressA, _accountBalance);

        worldState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 0, Eip2935TestConstants.Nonce);
        worldState.InsertCode(Eip2935Constants.BlockHashHistoryAddress, Eip2935TestConstants.CodeHash, Eip2935TestConstants.Code, _specProvider.GenesisSpec);

        worldState.CreateAccount(Eip4788Constants.BeaconRootsAddress, 0, Eip4788TestConstants.Nonce);
        worldState.InsertCode(Eip4788Constants.BeaconRootsAddress, Eip4788TestConstants.CodeHash, Eip4788TestConstants.Code, _specProvider.GenesisSpec);

        worldState.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, 0, Eip7002TestConstants.Nonce);
        worldState.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, Eip7002TestConstants.CodeHash, Eip7002TestConstants.Code, _specProvider.GenesisSpec);

        worldState.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, 0, Eip7251TestConstants.Nonce);
        worldState.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, Eip7251TestConstants.CodeHash, Eip7251TestConstants.Code, _specProvider.GenesisSpec);

        worldState.Commit(_specProvider.GenesisSpec);
        worldState.CommitTree(0);
        worldState.RecalculateStateRoot();
    }

    private static IEnumerable<TestCaseData> CodeTestSource
    {
        get
        {
            IEnumerable<AccountChanges> changes;
            UInt256 slot = 10;
            byte[] code = Prepare.EvmCode
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .Done;

            AccountChanges readAccount = Build.An.AccountChanges.WithAddress(_testAddress).WithStorageReads(slot).TestObject;
            changes = [readAccount];
            yield return new TestCaseData(code, changes) { TestName = "storage_read" };

            code = Prepare.EvmCode
                .PushData(slot)
                .PushData(slot)
                .Op(Instruction.SSTORE)
                .Done;
            changes = [Build.An.AccountChanges.WithAddress(_testAddress).WithStorageChanges(slot, [new(0, slot)]).TestObject];
            yield return new TestCaseData(code, changes) { TestName = "storage_write" };

            code = Prepare.EvmCode
                .PushData(slot)
                .PushData(slot)
                .Op(Instruction.SSTORE)
                .PushData(0)
                .PushData(slot)
                .Op(Instruction.SSTORE)
                .Done;
            changes = [readAccount];
            yield return new TestCaseData(code, changes) { TestName = "storage_write_return_to_original" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "extcodecopy" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "extcodehash" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "extcodesize" };

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.BALANCE)
                .Done;
            AccountChanges emptyTestAccount = Build.An.AccountChanges.WithAddress(_testAddress).TestObject;
            AccountChanges emptyBAccount = Build.An.AccountChanges.WithAddress(TestItem.AddressB).TestObject;
            changes = [emptyTestAccount, emptyBAccount];
            yield return new TestCaseData(code, changes) { TestName = "balance" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "selfdestruct" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "selfdestruct_oog" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "revert" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "delegations" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "call" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "call_oog" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "callcode" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "delgatecall" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "staticcall" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "create" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "create2" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "precompile" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "zero_transfer" };
        }
    }
}
