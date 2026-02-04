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
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
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
    private static readonly TestSpecProvider _specProvider = new(_spec);
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
