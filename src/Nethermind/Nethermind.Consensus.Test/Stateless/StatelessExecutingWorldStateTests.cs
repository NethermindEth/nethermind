// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

public class StatelessExecutingWorldStateTests
{
    /// <summary>Code loaded by hash after a bytecode access check must be the code stored under that hash.</summary>
    /// <remarks>The check remembers the code it loaded; a load of any other hash must still reach the store.</remarks>
    [Test]
    public void Code_by_hash_after_a_bytecode_check_is_the_stored_code([Values] bool loadCheckedCodeFirst)
    {
        byte[] checkedCode = [0x60, 0x01];
        byte[] otherCode = [0x60, 0x02];
        ValueHash256 checkedHash = ValueKeccak.Compute(checkedCode);
        ValueHash256 otherHash = ValueKeccak.Compute(otherCode);

        IWorldState inner = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = inner.BeginScope(IWorldState.PreGenesis);
        inner.CreateAccount(TestItem.AddressA, 0);
        inner.InsertCode(TestItem.AddressA, checkedCode, Prague.Instance);
        inner.CreateAccount(TestItem.AddressB, 0);
        inner.InsertCode(TestItem.AddressB, otherCode, Prague.Instance);

        StatelessExecutingWorldState state = new(inner);
        state.RecordBytecodeAccess(TestItem.AddressA);
        state.RecordBytecodeAccess(TestItem.AddressC);

        using (Assert.EnterMultipleScope())
        {
            if (loadCheckedCodeFirst)
                Assert.That(state.GetCode(in checkedHash).ToArray(), Is.EqualTo(checkedCode), "checked code");
            Assert.That(state.GetCode(in otherHash).ToArray(), Is.EqualTo(otherCode), "other code");
            Assert.That(state.GetCode(in checkedHash).ToArray(), Is.EqualTo(checkedCode), "checked code");
        }
    }

    /// <summary>EXTCODESIZE on a contract whose code the witness lacks must fail, whatever operation follows it.</summary>
    /// <remarks>The interpreter answers EXTCODESIZE followed by ISZERO, GT or EQ from the code hash alone; POP is the unfused control.</remarks>
    [Test]
    public void Extcodesize_of_code_missing_from_the_witness_fails([Values(Instruction.ISZERO, Instruction.GT, Instruction.EQ, Instruction.POP)] Instruction next)
    {
        IReleaseSpec spec = Prague.Instance;
        TestSpecProvider specProvider = new(spec);
        Address target = TestItem.AddressB;
        Address caller = TestItem.AddressC;
        byte[] targetCode = Prepare.EvmCode.Op(Instruction.STOP).Done;
        byte[] callerCode = Prepare.EvmCode.PushData(0).PushData(target).Op(Instruction.EXTCODESIZE).Op(next).Op(Instruction.STOP).Done;

        MemDb codeDb = new();
        IWorldState inner = TestWorldStateFactory.CreateForTest(codeDb: codeDb);
        BlockHeader parent;
        using (inner.BeginScope(IWorldState.PreGenesis))
        {
            inner.CreateAccount(TestItem.AddressA, 1.Ether);
            inner.CreateAccount(target, 0);
            inner.InsertCode(target, targetCode, spec);
            inner.CreateAccount(caller, 0);
            inner.InsertCode(caller, callerCode, spec);
            inner.Commit(spec);
            inner.CommitTree(0);
            parent = Build.A.BlockHeader.WithNumber(0).WithStateRoot(inner.StateRoot).TestObject;
        }

        codeDb.Remove(Keccak.Compute(targetCode).Bytes);
        StatelessExecutingWorldState state = new(inner);
        using IDisposable scope = state.BeginScope(parent);

        using EthereumVirtualMachine vm = new(new TestBlockhashProvider(specProvider), specProvider, LimboLogs.Instance);
        EthereumTransactionProcessor processor = new(BlobBaseFeeCalculator.Instance, specProvider, state, vm, new EthereumCodeInfoRepository(state), LimboLogs.Instance);
        Transaction tx = Build.A.Transaction.WithTo(caller).WithGasLimit(100_000).WithGasPrice(1)
            .SignedAndResolved(new EthereumEcdsa(specProvider.ChainId), TestItem.PrivateKeyA).TestObject;
        BlockHeader header = Build.A.BlockHeader.WithParent(parent).WithBaseFee(0).WithExcessBlobGas(0).TestObject;

        Assert.That(() => processor.Execute(tx, new BlockExecutionContext(header, spec), NullTxTracer.Instance),
            Throws.InvalidOperationException);
    }
}
