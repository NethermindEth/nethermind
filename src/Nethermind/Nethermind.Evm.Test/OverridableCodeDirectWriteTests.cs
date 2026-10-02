// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.OverridableEnv;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Code written straight to the world state, by SETCODEFROM (EIP-8298) or an EIP-7702 delegation, must be seen
/// through <see cref="OverridableCodeInfoRepository"/> in place of a code override set on the same account.
/// </summary>
public class OverridableCodeDirectWriteTests : VirtualMachineTestsBase
{
    public enum Writer { SetCodeFrom, Delegation }

    private const ulong GasLimit = 1_000_000;
    private const int OverrideResult = 7;
    private const int SourceResult = 42;

    private static readonly PrivateKey TargetKey = TestItem.PrivateKeyF;
    private static readonly Address Target = TargetKey.Address;
    private static readonly Address Source = TestItem.AddressC;
    private static readonly Address PreviousDelegate = TestItem.AddressE;
    private static readonly Address Wrapper = new("0x1000000000000000000000000000000000000001");
    private static readonly Address MovedPrecompile = new("0x2000000000000000000000000000000000000002");
    private static readonly byte[] SourceCode = Returning(SourceResult);

    private ITransactionProcessor _plainProcessor = null!;
    private ITransactionProcessor _overridingProcessor = null!;
    private OverridableCodeInfoRepository _repository = null!;

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;
    protected override ISpecProvider SpecProvider => new TestSpecProvider(new Amsterdam { IsEip8298Enabled = true });

    public override void Setup()
    {
        base.Setup();
        _plainProcessor = _processor;
        _repository = new OverridableCodeInfoRepository(CodeInfoRepository, TestState);
        _overridingProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, Machine, _repository, LimboLogs.Instance);
        Deploy(Source, SourceCode);
        Deploy(PreviousDelegate, Returning(OverrideResult));
    }

    /// <remarks>Without overrides the plain repository runs the same scenario, so both must agree.</remarks>
    [TestCase(Writer.SetCodeFrom, true)]
    [TestCase(Writer.Delegation, true)]
    [TestCase(Writer.SetCodeFrom, false)]
    [TestCase(Writer.Delegation, false)]
    public void DirectWrite_ReadsSeeTheWrittenCode(Writer writer, bool overridden)
    {
        byte[] codeBefore = CodeBefore(writer);
        TestState.CreateAccount(Target, 1.Ether);
        if (overridden) Override(Target, codeBefore);
        else TestState.InsertCode(Target, codeBefore, Spec);

        Prepare observer = writer == Writer.SetCodeFrom ? Prepare.EvmCode.Call(Target, 100_000).POP() : Prepare.EvmCode;
        Run(writer, overridden, Observe(observer).Done);

        byte[] written = CodeAfter(writer);
        AssertStorage(1, Keccak.Compute(written));
        AssertStorage(2, (UInt256)written.Length);
        AssertStorage(3, (UInt256)SourceResult);
    }

    [Test]
    public void DirectWrite_RevertedBySnapshot_RestoresTheOverride([Values] Writer writer, [Values] bool traceAccess)
    {
        BlockAccessListAtIndex accesses = new();
        if (traceAccess)
        {
            TracedAccessWorldState tracedState = new(TestState, false);
            tracedState.SetGeneratingBlockAccessList(accesses);
            _repository = new OverridableCodeInfoRepository(CodeInfoRepository, tracedState);
        }
        byte[] codeBefore = CodeBefore(writer);
        TestState.CreateAccount(Target, 1.Ether);
        Override(Target, codeBefore);
        CodeInfo original = _repository.GetCachedCodeInfoNoDelegation(Target, Spec);
        Snapshot snapshot = TestState.TakeSnapshot();

        if (writer == Writer.SetCodeFrom) TestState.InsertCode(Target, Keccak.Compute(SourceCode), SourceCode, Spec);
        else _repository.SetDelegation(Source, Target, Spec);

        Assert.That(_repository.GetCachedCodeInfoNoDelegation(Target, Spec).Code.ToArray(), Is.EqualTo(CodeAfter(writer)), "after the write");

        TestState.Restore(snapshot);

        Assert.That(_repository.GetCachedCodeInfoNoDelegation(Target, Spec), Is.SameAs(original), "after the revert");
        if (traceAccess)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(accesses.AccountCount, Is.EqualTo(1));
                Assert.That(accesses.HasAccount(Target), Is.True);
            }
        }
    }

    [Test]
    public void PrecompileOverride_DirectWriteAndRevert_KeepDispatchConsistent()
    {
        TestState.CreateAccount(Target, 1.Ether);
        CodeInfo precompile = new(Sha256Precompile.Instance);
        _repository.SetCodeOverride(Spec, Target, precompile);
        Snapshot snapshot = TestState.TakeSnapshot();
        Assert.That(_repository.GetPrecompile(Target, Spec), Is.SameAs(precompile.Precompile));

        TestState.InsertCode(Target, SourceCode, Spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_repository.GetPrecompile(Target, Spec), Is.Null);
            Assert.That(_repository.GetCachedCodeInfoNoDelegation(Target, Spec).Code.ToArray(), Is.EqualTo(SourceCode));
        }

        TestState.Restore(snapshot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_repository.GetPrecompile(Target, Spec), Is.SameAs(precompile.Precompile));
            Assert.That(_repository.GetCachedCodeInfoNoDelegation(Target, Spec), Is.SameAs(precompile));
        }
    }

    [Test]
    public void SetCodeFrom_InRevertedFrame_RestoresTheOverride()
    {
        byte[] codeBefore = CodeBefore(Writer.SetCodeFrom);
        TestState.CreateAccount(Target, 1.Ether);
        Override(Target, codeBefore);
        Deploy(Wrapper, Prepare.EvmCode.Call(Target, 100_000).POP().Revert(0, 0).Done);

        Run(Writer.SetCodeFrom, true, Observe(Prepare.EvmCode.Call(Wrapper, 200_000).POP()).Done);

        AssertStorage(1, Keccak.Compute(codeBefore));
        AssertStorage(2, (UInt256)codeBefore.Length);
        AssertStorage(3, (UInt256)OverrideResult);
    }

    /// <remarks>The adopted code must not resurrect the precompile the override shadowed.</remarks>
    [Test]
    public void SetCodeFrom_OnOverriddenPrecompileAddress_LaterCallRunsTheAdoptedCode()
    {
        Override(Sha256Precompile.Address, CodeBefore(Writer.SetCodeFrom));

        Run(Writer.SetCodeFrom, true, Prepare.EvmCode
            .Call(Sha256Precompile.Address, 100_000).POP()
            .Call(Sha256Precompile.Address, 100_000).POP()
            .PushData(32).PushData(0).PushData(0).Op(Instruction.RETURNDATACOPY)
            .PushData(0).Op(Instruction.MLOAD).PushData(3).Op(Instruction.SSTORE)
            .STOP().Done);

        AssertStorage(3, (UInt256)SourceResult);
    }

    /// <remarks>
    /// The repository serves the moved precompile at its new address, so, as at a precompile's own address,
    /// SETCODEFROM validates and adopts the code held in state there, which pairs with the hash it installs.
    /// </remarks>
    [TestCase(false, TestName = "SetCodeFrom_FromMovedPrecompileTarget_UsesTheCodeInState(deployed code)")]
    [TestCase(true, TestName = "SetCodeFrom_FromMovedPrecompileTarget_UsesTheCodeInState(delegation designator)")]
    public void SetCodeFrom_FromMovedPrecompileTarget_UsesTheCodeInState(bool delegated)
    {
        byte[] targetCode = delegated ? [.. Eip7702Constants.DelegationHeader, .. Source.Bytes] : SourceCode;
        Deploy(MovedPrecompile, targetCode);
        TestState.ApplyStateOverridesNoCommit(_repository,
            new Dictionary<Address, AccountOverride> { [Sha256Precompile.Address] = new() { MovePrecompileToAddress = MovedPrecompile } }, Spec);
        byte[] observerCode = Prepare.EvmCode.SETCODEFROM(MovedPrecompile).PushData(0).Op(Instruction.SSTORE).STOP().Done;

        Run(Writer.SetCodeFrom, true, observerCode);

        byte[] expectedCode = delegated ? observerCode : SourceCode;
        AssertStorage(0, delegated ? UInt256.Zero : UInt256.One);
        AssertCodeHash(Recipient, Keccak.Compute(expectedCode));
        Assert.That(_repository.GetCachedCodeInfoNoDelegation(Recipient, Spec).Code.ToArray(), Is.EqualTo(expectedCode), "code served");
    }

    private static byte[] Returning(int value) => Prepare.EvmCode.PushData(value).MSTORE(0).Return(32, 0).Done;

    private static byte[] CodeBefore(Writer writer) => writer == Writer.SetCodeFrom
        ? Prepare.EvmCode.SETCODEFROM(Source).POP().PushData(OverrideResult).MSTORE(0).Return(32, 0).Done
        : [.. Eip7702Constants.DelegationHeader, .. PreviousDelegate.Bytes];

    private static byte[] CodeAfter(Writer writer) => writer == Writer.SetCodeFrom
        ? SourceCode
        : [.. Eip7702Constants.DelegationHeader, .. Source.Bytes];

    /// <summary>Stores the target's EXTCODEHASH, EXTCODESIZE and call result in slots 1, 2 and 3.</summary>
    private static Prepare Observe(Prepare prefix) => prefix
        .EXTCODEHASH(Target).PushData(1).Op(Instruction.SSTORE)
        .PushData(Target).Op(Instruction.EXTCODESIZE).PushData(2).Op(Instruction.SSTORE)
        .Call(Target, 100_000).POP()
        .PushData(32).PushData(0).PushData(0).Op(Instruction.RETURNDATACOPY)
        .PushData(0).Op(Instruction.MLOAD).PushData(3).Op(Instruction.SSTORE)
        .STOP();

    private void Deploy(Address address, byte[] code)
    {
        TestState.CreateAccount(address, 1.Ether);
        TestState.InsertCode(address, code, Spec);
    }

    private void Override(Address address, byte[] code) => TestState.ApplyStateOverridesNoCommit(_repository,
        new Dictionary<Address, AccountOverride> { [address] = new() { Code = code } }, Spec);

    private void Run(Writer writer, bool overridden, byte[] observerCode)
    {
        Transaction? transaction = null;
        if (writer == Writer.Delegation)
        {
            EthereumEcdsa ecdsa = new(SpecProvider.ChainId);
            transaction = Build.A.Transaction
                .WithType(TxType.SetCode)
                .To(Recipient)
                .WithGasLimit(GasLimit)
                .WithGasPrice(1)
                .WithNonce(TestState.AccountExists(Sender) ? TestState.GetNonce(Sender) : 0UL)
                .WithAuthorizationCode(ecdsa.Sign(TargetKey, SpecProvider.ChainId, Source, 0))
                .SignedAndResolved(ecdsa, SenderKey, true)
                .TestObject;
        }

        (Block block, Transaction tx) = PrepareTx(Activation, GasLimit, observerCode, transaction: transaction);
        TestAllTracerWithOutput tracer = CreateTracer();
        ITransactionProcessor processor = overridden ? _overridingProcessor : _plainProcessor;
        processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), tracer.Error);
    }
}
