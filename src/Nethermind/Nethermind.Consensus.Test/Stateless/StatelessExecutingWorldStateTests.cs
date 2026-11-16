// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Precompiles;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
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
    /// <remarks>
    /// The interpreter answers EXTCODESIZE followed by ISZERO, GT or EQ from the code hash alone; POP is the unfused control.
    /// The code is read before the folded operation is charged, so it must still fail when that charge runs out of gas.
    /// </remarks>
    [Test]
    public void Extcodesize_of_code_missing_from_the_witness_fails(
        [Values(Instruction.ISZERO, Instruction.GT, Instruction.EQ, Instruction.POP)] Instruction next,
        [Values] bool outOfGasOnNext)
    {
        // Intrinsic cost, then PUSH1, PUSH20 and a cold EXTCODESIZE, leaving nothing for the next operation.
        ulong gasLimit = outOfGasOnNext ? GasCostOf.Transaction + 2 * GasCostOf.VeryLow + GasCostOf.ColdAccountAccess : 100_000;

        Assert.That(() => ExecuteExtcodesize(next, Prepare.EvmCode.Op(Instruction.STOP).Done, gasLimit), Throws.InvalidOperationException);
    }

    [Test]
    public void Extcodesize_of_an_account_without_code_needs_no_code_in_the_witness(
        [Values(Instruction.ISZERO, Instruction.GT, Instruction.EQ, Instruction.POP)] Instruction next) =>
        Assert.That(() => ExecuteExtcodesize(next, targetCode: null, gasLimit: 100_000), Throws.Nothing);

    /// <summary>
    /// A call to a precompile's address runs the precompile, but EXTCODESIZE reads the code an account there holds, so
    /// that code must be in the witness too, whatever operation follows.
    /// </summary>
    [Test]
    public void Extcodesize_of_code_at_a_precompile_address_missing_from_the_witness_fails(
        [Values(Instruction.ISZERO, Instruction.GT, Instruction.EQ, Instruction.POP)] Instruction next) =>
        Assert.That(() => ExecuteExtcodesize(next, Prepare.EvmCode.Op(Instruction.STOP).Done, 100_000, PrecompiledAddresses.Identity), Throws.InvalidOperationException);

    /// <summary>
    /// EXTCODESIZE and EXTCODECOPY of a precompile's address read the account there, code or none, so the witness of
    /// a transaction running them proves that account, present or absent, also when block processing captures it
    /// through its precompile cache, and stateless execution of that witness finds no code.
    /// </summary>
    [Test]
    public void Witness_of_a_code_read_at_a_precompile_address_without_code_proves_the_account(
        [Values] bool copy, [Values] bool accountExists, [Values] bool precompileCache, [Values] bool guest)
    {
        IReleaseSpec spec = Prague.Instance;
        TestSpecProvider specProvider = new(spec);
        Address target = PrecompiledAddresses.Identity;
        Address caller = TestItem.AddressC;
        // The copy goes over a filled word, so its zeros show; EXTCODESIZE followed by PUSH1 does not fuse.
        byte[] callerCode = (copy
                ? Prepare.EvmCode.PushData(UInt256.MaxValue).PushData(0).Op(Instruction.MSTORE)
                    .PushData(32).PushData(0).PushData(0).PushData(target).Op(Instruction.EXTCODECOPY)
                : Prepare.EvmCode.PushData(target).Op(Instruction.EXTCODESIZE).PushData(0).Op(Instruction.MSTORE))
            .Return(32, 0).Done;

        using IDbProvider dbProvider = TestMemDbProvider.Init();
        WorldStateManager manager = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, LimboLogs.Instance);
        IWorldState inner = new WorldState(manager.GlobalWorldState, LimboLogs.Instance);
        BlockHeader parent;
        using (inner.BeginScope(IWorldState.PreGenesis))
        {
            inner.CreateAccount(TestItem.AddressA, 1.Ether);
            if (accountExists) inner.CreateAccount(target, 1);
            // Enough other accounts that the proofs the transaction needs anyway do not cover the target's path.
            for (int i = 0; i < 100; i++) inner.CreateAccount(Address.FromNumber((UInt256)(0x1000 + i)), 1);
            inner.CreateAccount(caller, 0);
            inner.InsertCode(caller, callerCode, spec);
            inner.Commit(spec);
            inner.CommitTree(0);
            parent = Build.A.BlockHeader.WithNumber(0).WithStateRoot(inner.StateRoot).TestObject;
        }

        BlockHeader header = Build.A.BlockHeader.WithParent(parent).WithBaseFee(0).WithExcessBlobGas(0).TestObject;
        Transaction tx = Build.A.Transaction.WithTo(caller).WithGasLimit(100_000).WithGasPrice(1)
            .SignedAndResolved(new EthereumEcdsa(specProvider.ChainId), TestItem.PrivateKeyA).TestObject;
        IHeaderFinder headerFinder = Substitute.For<IHeaderFinder>();
        headerFinder.Get(parent.Hash!, Arg.Any<ulong?>()).Returns(parent);
        WitnessGeneratingWorldState capturing = new(inner, manager.GlobalStateReader, manager.CreateReadOnlyTrieStore(), new WitnessHeaderRecorder(), headerFinder);
        Witness witness;
        using (capturing.BeginScope(parent))
        {
            Assert.That(Run(capturing, precompileCache), Is.EqualTo(new byte[32]), "result while capturing");
            capturing.Commit(spec);
            witness = capturing.GetWitness(parent);
        }

        using (witness)
        {
            Assert.That(witness.Keys, Has.Some.EqualTo(target.Bytes.ToArray()), "account recorded");
            IWorldState stateless = guest
                ? new GuestWorldState(new TrieStoreScopeProvider(WitnessNodeStorage.CreateTrieStore(witness.State), witness.CreateCodeDb(), UnavailableStateHeaderProvider.Instance, LimboLogs.Instance))
                : new StatelessBlockProcessingEnv(witness, specProvider, Always.Valid, LimboLogs.Instance).WorldState;
            using IDisposable scope = stateless.BeginScope(parent);
            using (Assert.EnterMultipleScope())
            {
                // Read through the witness alone, so a node missing from the account's path throws.
                Assert.That(stateless.GetCodeHash(target), Is.EqualTo(Keccak.OfAnEmptyString.ValueHash256), "account proved");
                Assert.That(Run(stateless, withPrecompileCache: false), Is.EqualTo(new byte[32]), "result in stateless execution");
            }
        }

        byte[] Run(IWorldState state, bool withPrecompileCache)
        {
            using EthereumVirtualMachine vm = new(new TestBlockhashProvider(specProvider), specProvider, LimboLogs.Instance);
            ICodeInfoRepository codeInfoRepository = new CacheCodeInfoRepository(state, new EthereumPrecompileProvider(), NoopCodeCache.Instance);
            if (withPrecompileCache)
                codeInfoRepository = new PrecompileCachedCodeInfoRepository(state, new EthereumPrecompileProvider(), codeInfoRepository, precompileCaches: null);
            EthereumTransactionProcessor processor = new(BlobBaseFeeCalculator.Instance, specProvider, state, vm, codeInfoRepository, LimboLogs.Instance);
            CallOutputTracer tracer = new();
            processor.Execute(tx, new BlockExecutionContext(header, spec), tracer);
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), tracer.Error);
            return tracer.ReturnValue;
        }
    }

    /// <summary>Runs a transaction calling a contract that applies EXTCODESIZE to a target and then <paramref name="next"/>.</summary>
    /// <param name="targetCode">The target's code, removed from the code database before execution; <see langword="null"/> for an account without code.</param>
    /// <param name="targetAddress">The target; an ordinary account when <see langword="null"/>.</param>
    private static void ExecuteExtcodesize(Instruction next, byte[] targetCode, ulong gasLimit, Address targetAddress = null)
    {
        IReleaseSpec spec = Prague.Instance;
        TestSpecProvider specProvider = new(spec);
        Address target = targetAddress ?? TestItem.AddressB;
        Address caller = TestItem.AddressC;
        byte[] callerCode = Prepare.EvmCode.PushData(0).PushData(target).Op(Instruction.EXTCODESIZE).Op(next).Op(Instruction.STOP).Done;

        MemDb codeDb = new();
        IWorldState inner = TestWorldStateFactory.CreateForTest(codeDb: codeDb);
        BlockHeader parent;
        using (inner.BeginScope(IWorldState.PreGenesis))
        {
            inner.CreateAccount(TestItem.AddressA, 1.Ether);
            inner.CreateAccount(target, 1);
            if (targetCode is not null)
                inner.InsertCode(target, targetCode, spec);
            inner.CreateAccount(caller, 0);
            inner.InsertCode(caller, callerCode, spec);
            inner.Commit(spec);
            inner.CommitTree(0);
            parent = Build.A.BlockHeader.WithNumber(0).WithStateRoot(inner.StateRoot).TestObject;
        }

        if (targetCode is not null)
            codeDb.Remove(Keccak.Compute(targetCode).Bytes);
        StatelessExecutingWorldState state = new(inner);
        using IDisposable scope = state.BeginScope(parent);

        using EthereumVirtualMachine vm = new(new TestBlockhashProvider(specProvider), specProvider, LimboLogs.Instance);
        EthereumTransactionProcessor processor = new(BlobBaseFeeCalculator.Instance, specProvider, state, vm, new CacheCodeInfoRepository(state, new EthereumPrecompileProvider(), NoopCodeCache.Instance), LimboLogs.Instance);
        Transaction tx = Build.A.Transaction.WithTo(caller).WithGasLimit(gasLimit).WithGasPrice(1)
            .SignedAndResolved(new EthereumEcdsa(specProvider.ChainId), TestItem.PrivateKeyA).TestObject;
        BlockHeader header = Build.A.BlockHeader.WithParent(parent).WithBaseFee(0).WithExcessBlobGas(0).TestObject;

        processor.Execute(tx, new BlockExecutionContext(header, spec), NullTxTracer.Instance);
    }
}
