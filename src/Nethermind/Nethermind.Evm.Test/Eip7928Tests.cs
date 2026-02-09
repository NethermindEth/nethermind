// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class Eip7928Tests() : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    private static readonly EthereumEcdsa _ecdsa = new(0);
    private static readonly UInt256 _accountBalance = 10.Ether();
    private static readonly long _gasLimit = 100000;
    private static readonly Address _testAddress = ContractAddress.From(TestItem.AddressA, 0);

    [TestCaseSource(nameof(CodeTestSource))]
    public async Task Constructs_BAL_when_processing_code(byte[] code, IEnumerable<AccountChanges> expected)
    {
        InitWorldState(TestState);
        ParallelWorldState worldState = TestState as ParallelWorldState;
        worldState.TracingEnabled = true;

        Transaction createTx = Build.A.Transaction.WithCode(code).WithGasLimit(_gasLimit).WithValue(0).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;
        Block block = Build.A.Block.TestObject;

        _processor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Amsterdam.Instance));
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

    private Action<ContainerBuilder> BuildContainer()
        => containerBuilder => containerBuilder.AddSingleton(SpecProvider);

    private void InitWorldState(IWorldState worldState)
    {
        worldState.CreateAccount(TestItem.AddressA, _accountBalance);

        worldState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 0, Eip2935TestConstants.Nonce);
        worldState.InsertCode(Eip2935Constants.BlockHashHistoryAddress, Eip2935TestConstants.CodeHash, Eip2935TestConstants.Code, SpecProvider.GenesisSpec);

        worldState.CreateAccount(Eip4788Constants.BeaconRootsAddress, 0, Eip4788TestConstants.Nonce);
        worldState.InsertCode(Eip4788Constants.BeaconRootsAddress, Eip4788TestConstants.CodeHash, Eip4788TestConstants.Code, SpecProvider.GenesisSpec);

        worldState.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, 0, Eip7002TestConstants.Nonce);
        worldState.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, Eip7002TestConstants.CodeHash, Eip7002TestConstants.Code, SpecProvider.GenesisSpec);

        worldState.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, 0, Eip7251TestConstants.Nonce);
        worldState.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, Eip7251TestConstants.CodeHash, Eip7251TestConstants.Code, SpecProvider.GenesisSpec);

        worldState.Commit(SpecProvider.GenesisSpec);
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

            AccountChanges readAccount = Build.An.AccountChanges.WithAddress(_testAddress).WithStorageReads(slot).WithNonceChanges([new(0, 1)]).TestObject;
            changes = [readAccount];
            yield return new TestCaseData(code, changes) { TestName = "storage_read" };

            code = Prepare.EvmCode
                .PushData(slot)
                .PushData(slot)
                .Op(Instruction.SSTORE)
                .Done;
            changes = [Build.An.AccountChanges.WithAddress(_testAddress).WithStorageChanges(slot, [new(0, slot)]).WithNonceChanges([new(0, 1)]).TestObject];
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

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.BALANCE)
                .Done;
            AccountChanges testAccount = Build.An.AccountChanges.WithAddress(_testAddress).WithNonceChanges([new(0, 1)]).TestObject;
            AccountChanges emptyBAccount = new(TestItem.AddressB);
            changes = [testAccount, emptyBAccount];
            yield return new TestCaseData(code, changes) { TestName = "balance" };

            code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressB)
                .Op(Instruction.EXTCODECOPY)
                .Done;
            changes = [testAccount, emptyBAccount];
            yield return new TestCaseData(code, changes) { TestName = "extcodecopy" };

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.EXTCODEHASH)
                .Done;
            changes = [testAccount, emptyBAccount];
            yield return new TestCaseData(code, changes) { TestName = "extcodehash" };

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.EXTCODESIZE)
                .Done;
            changes = [testAccount, emptyBAccount];
            yield return new TestCaseData(code, changes) { TestName = "extcodesize" };

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.SELFDESTRUCT)
                .Done;
            changes = [new(_testAddress), emptyBAccount];
            yield return new TestCaseData(code, changes) { TestName = "selfdestruct" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "selfdestruct_oog" };

            code = Prepare.EvmCode
                .PushData(slot)
                .PushData(slot)
                .Op(Instruction.SSTORE)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.REVERT)
                .Done;
            // revert should convert storage load to read
            changes = [Build.An.AccountChanges.WithAddress(_testAddress).WithStorageReads(slot).TestObject];
            yield return new TestCaseData(code, changes) { TestName = "revert" };

            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "delegations" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "call" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "call_oog" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "callcode" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "delegatecall" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "staticcall" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "create" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "create2" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "precompile" };
            // yield return new TestCaseData(code, new Dictionary<Address, AccountChanges>{{_testAddress, readAccount}}) { TestName = "zero_transfer" };
        }
    }
}
