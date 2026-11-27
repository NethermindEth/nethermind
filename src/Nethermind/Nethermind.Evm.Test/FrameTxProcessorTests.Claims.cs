// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Precompiles;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>EIP-8141 claims verified by a custom verifier in the pure context, and the default code's claim lookup.</summary>
public partial class FrameTxProcessorTests
{
    /// <summary>Every opcode that reads the call's context, another account, the chain, the block or the transaction
    /// halts the verifier, and with it the claim.</summary>
    [TestCase(Instruction.ADDRESS)]
    [TestCase(Instruction.BALANCE)]
    [TestCase(Instruction.ORIGIN)]
    [TestCase(Instruction.CALLER)]
    [TestCase(Instruction.GASPRICE)]
    [TestCase(Instruction.EXTCODESIZE)]
    [TestCase(Instruction.EXTCODECOPY)]
    [TestCase(Instruction.EXTCODEHASH)]
    [TestCase(Instruction.BLOCKHASH)]
    [TestCase(Instruction.COINBASE)]
    [TestCase(Instruction.TIMESTAMP)]
    [TestCase(Instruction.NUMBER)]
    [TestCase(Instruction.PREVRANDAO)]
    [TestCase(Instruction.GASLIMIT)]
    [TestCase(Instruction.CHAINID)]
    [TestCase(Instruction.SELFBALANCE)]
    [TestCase(Instruction.BASEFEE)]
    [TestCase(Instruction.BLOBHASH)]
    [TestCase(Instruction.BLOBBASEFEE)]
    [TestCase(Instruction.SLOTNUM)]
    [TestCase(Instruction.SLOAD)]
    [TestCase(Instruction.TLOAD)]
    [TestCase(Instruction.TXPARAM)]
    [TestCase(Instruction.FRAMEPARAM)]
    [TestCase(Instruction.HASCLAIM)]
    [TestCase(Instruction.APPROVE)]
    // An opcode a later EIP introduces halts until that EIP admits it.
    [TestCase(Instruction.TXTRACE)]
    public void PureVerifier_OpcodeOutsideThePureContext_InvalidatesTheClaim(Instruction opcode) =>
        AssertPureVerifier(Prepare.EvmCode.PushData(0).PushData(0).PushData(0).PushData(0).Op(opcode).Op(Instruction.STOP).Done, holds: false);

    /// <summary>A verifier computes over its input, its gas and its own code: what it may read of them runs.</summary>
    [TestCase(Instruction.CALLVALUE)]
    [TestCase(Instruction.CALLDATASIZE)]
    [TestCase(Instruction.CALLDATALOAD)]
    [TestCase(Instruction.CODESIZE)]
    [TestCase(Instruction.GAS)]
    [TestCase(Instruction.RETURNDATASIZE)]
    [TestCase(Instruction.MSIZE)]
    [TestCase(Instruction.KECCAK256)]
    public void PureVerifier_OpcodeInsideThePureContext_Runs(Instruction opcode) =>
        AssertPureVerifier(Prepare.EvmCode.PushData(0).PushData(0).Op(opcode).Op(Instruction.STOP).Done, holds: true);

    [Test]
    public void PureVerifier_CallValueIsZero() =>
        AssertPureVerifier(BranchOnStackTop(
            Prepare.EvmCode.Op(Instruction.CALLVALUE).Op(Instruction.ISZERO).Done,
            Prepare.EvmCode.Op(Instruction.STOP).Done,
            Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done), holds: true);

    [Test]
    public void PureVerifier_WriteForbiddenInAStaticCall_InvalidatesTheClaim() =>
        AssertPureVerifier(Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done, holds: false);

    [TestCase(Instruction.CALL)]
    [TestCase(Instruction.CALLCODE)]
    [TestCase(Instruction.DELEGATECALL)]
    public void PureVerifier_CallOtherThanStaticCall_InvalidatesTheClaim(Instruction call)
    {
        Prepare code = call switch
        {
            Instruction.CALL => Prepare.EvmCode.Call(IdentityPrecompile.Address, 10_000),
            Instruction.CALLCODE => Prepare.EvmCode.CallCode(IdentityPrecompile.Address, 10_000),
            _ => Prepare.EvmCode.DelegateCall(IdentityPrecompile.Address, 10_000),
        };

        AssertPureVerifier(code.Op(Instruction.POP).Op(Instruction.STOP).Done, holds: false);
    }

    /// <summary>A static call may reach an active precompile and nothing else: not a contract, nor an empty account.</summary>
    [TestCase(true, true, TestName = "PureVerifier_StaticCallToAPrecompile_Runs")]
    [TestCase(false, true, TestName = "PureVerifier_StaticCallToAContract_InvalidatesTheClaim")]
    [TestCase(false, false, TestName = "PureVerifier_StaticCallToAnEmptyAccount_InvalidatesTheClaim")]
    public void PureVerifier_StaticCall(bool precompile, bool deployed)
    {
        if (!precompile && deployed) DeployContract(Recipient, Prepare.EvmCode.Op(Instruction.STOP).Done);
        Address target = precompile ? IdentityPrecompile.Address : Recipient;

        AssertPureVerifier(Prepare.EvmCode.StaticCall(target, 10_000).Op(Instruction.POP).Op(Instruction.STOP).Done, holds: precompile);
    }

    /// <summary>The claim holds if and only if the call succeeds, whatever it returns.</summary>
    [TestCase(false, true, TestName = "PureVerifier_ReturningZero_Holds")]
    [TestCase(true, false, TestName = "PureVerifier_Reverting_DoesNotHold")]
    public void PureVerifier_ClaimHoldsIfTheCallSucceeds(bool reverts, bool holds) =>
        AssertPureVerifier(reverts
            ? Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done
            : Prepare.EvmCode.PushData(0).PushData(0).PushData(0).Op(Instruction.MSTORE).PushData(32).PushData(0).Op(Instruction.RETURN).Done, holds);

    /// <summary>The verifier is called with <c>resolved_key || resolved_msg || witness</c>: the sender and the canonical
    /// signature hash for an empty key and msg.</summary>
    [Test]
    public void PureVerifier_ReceivesTheResolvedKeyAndMsgBeforeTheWitness()
    {
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.FrameClaims = [CustomClaim(Verifier, witnessLength: 1)];
        tx.ClaimWitnesses = [[0x2a]];
        UInt256 sigHash = new(FrameTxSigHash.ComputeValue(tx).Bytes, isBigEndian: true);
        byte[] condition = Prepare.EvmCode
            .PushData(0).Op(Instruction.CALLDATALOAD).PushData(AddressAsWord(Sender)).Op(Instruction.EQ)
            .PushData(32).Op(Instruction.CALLDATALOAD).PushData(sigHash).Op(Instruction.EQ).Op(Instruction.AND)
            .PushData(64).Op(Instruction.CALLDATALOAD).PushData(0).Op(Instruction.BYTE).PushData(0x2a).Op(Instruction.EQ).Op(Instruction.AND)
            .Op(Instruction.CALLDATASIZE).PushData(65).Op(Instruction.EQ).Op(Instruction.AND).Done;
        DeployContract(Verifier, BranchOnStackTop(condition,
            Prepare.EvmCode.Op(Instruction.STOP).Done,
            Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done));

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.True, result.ErrorDescription);
    }

    [TestCase(false, TestName = "PureVerifier_AccountWithoutCode_InvalidatesTheClaim")]
    [TestCase(true, TestName = "PureVerifier_Eip7702DelegatedAccount_InvalidatesTheClaim")]
    public void PureVerifier_WithoutCodeOfItsOwn_InvalidatesTheClaim(bool delegated)
    {
        DeployContract(Recipient, Prepare.EvmCode.Op(Instruction.STOP).Done);
        if (delegated) DeployContract(Verifier, [.. Eip7702Constants.DelegationHeader, .. Recipient.Bytes]);

        AssertClaim(CustomClaim(Verifier), [], holds: false);
    }

    /// <summary>An active precompile named as the verifier is dispatched directly, and its failure is the claim's.</summary>
    [TestCase(true, TestName = "PureVerifier_PrecompileThatSucceeds_Holds")]
    [TestCase(false, TestName = "PureVerifier_PrecompileThatFails_DoesNotHold")]
    public void PureVerifier_Precompile(bool succeeds) =>
        // Identity accepts any input; point evaluation fails on any input that is not 192 bytes long.
        AssertClaim(CustomClaim(succeeds ? IdentityPrecompile.Address : PrecompiledAddresses.PointEvaluation), [], holds: succeeds);

    /// <summary>The verifier's own access is drawn from its gas limit, cold as its access set starts empty but for the
    /// precompiles.</summary>
    [TestCase(0, true, TestName = "PureVerifier_GasLimitCoveringItsColdAccess_Holds")]
    [TestCase(1, false, TestName = "PureVerifier_GasLimitBelowItsColdAccess_DoesNotHold")]
    public void PureVerifier_EntryAccessIsDrawnFromItsGasLimit(int shortfall, bool holds)
    {
        DeployContract(Verifier, Prepare.EvmCode.Op(Instruction.STOP).Done);

        AssertClaim(CustomClaim(Verifier, gasLimit: Spec.GasCosts.ColdAccountAccessCost - (ulong)shortfall), [], holds);
    }

    /// <summary>The full gas limit is charged whatever the verifier uses, so no charge depends on the witness.</summary>
    [Test]
    public void PureVerifier_ChargesItsFullGasLimit()
    {
        DeployContract(Verifier, Prepare.EvmCode.Op(Instruction.STOP).Done);

        long lower = ClaimGasUsed(CustomClaim(Verifier, gasLimit: 50_000));
        long higher = ClaimGasUsed(CustomClaim(Verifier, gasLimit: 60_000));

        Assert.That(higher - lower, Is.EqualTo(10_000));
    }

    /// <summary>Verification has its own access sets, discarded on exit, so it warms nothing a frame then reads.</summary>
    [Test]
    public void PureVerifier_WarmsNothingForTheFrames()
    {
        DeployContract(Verifier, Prepare.EvmCode.Op(Instruction.STOP).Done);
        // Stores the gas a BALANCE of the verifier costs.
        DeployContract(Observer, Prepare.EvmCode
            .Op(Instruction.GAS).PushData(Verifier).Op(Instruction.BALANCE).Op(Instruction.POP).Op(Instruction.GAS)
            .Op(Instruction.SWAP1).Op(Instruction.SUB).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        UInt256 Measure(bool withClaim)
        {
            DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
            Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(FrameMode.Default, target: Observer));
            if (withClaim)
            {
                tx.FrameClaims = [CustomClaim(Verifier)];
                tx.ClaimWitnesses = [[]];
            }

            Assert.That(Process(tx).TransactionExecuted, Is.True);
            return StorageAt(new StorageCell(Observer, 0));
        }

        Assert.That(Measure(withClaim: true), Is.EqualTo(Measure(withClaim: false)));
    }

    /// <summary>The default code refuses a <c>VERIFY</c> frame carrying data, even with the claim it needs.</summary>
    [Test]
    public void Execute_DefaultCodeVerifyWithFrameData_Reverts([Values] bool withData)
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
        TxFrame verify = new(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, withData ? new byte[] { 1 } : default(ReadOnlyMemory<byte>));
        Transaction tx = FrameTx(nonce: 0, verify);
        SignCanonicalHash(tx, index: 0, TestItem.PrivateKeyA, signer: null);

        TransactionResult result = Process(tx);

        Assert.That(result.TransactionExecuted, Is.EqualTo(!withData), result.ErrorDescription);
    }

    /// <summary>A missing witness list is not checked: only a replay of a validated block, whose witnesses the dependency
    /// proof was checked for, or a call runs without one.</summary>
    [Test]
    public void Execute_ClaimsWithoutAWitnessList_AreNotChecked()
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.FrameClaims = [FrameTxTestFrames.Secp256k1Claim(null)];

        Assert.That(Process(tx).TransactionExecuted, Is.True);
    }

    private void AssertPureVerifier(byte[] code, bool holds)
    {
        DeployContract(Verifier, code);
        AssertClaim(CustomClaim(Verifier), [], holds);
    }

    private void AssertClaim(TxFrameClaim claim, byte[] witness, bool holds)
    {
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.FrameClaims = [claim];
        tx.ClaimWitnesses = [witness];

        TransactionResult result = Process(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.EqualTo(holds), result.ErrorDescription);
            if (!holds)
            {
                Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
                Assert.That(result.ErrorDescription, Does.Contain(FrameTxClaimVerifier.InvalidClaim));
            }
        }
    }

    private long ClaimGasUsed(TxFrameClaim claim)
    {
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.FrameClaims = [claim];
        tx.ClaimWitnesses = [[]];
        GasUsedTracer tracer = new();

        Assert.That(Process(tx, tracer: tracer).TransactionExecuted, Is.True);
        return tracer.GasUsed;
    }

    private sealed class GasUsedTracer : TxTracer
    {
        public override bool IsTracingReceipt => true;
        public long GasUsed { get; private set; }

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null) =>
            GasUsed = (long)gasSpent.SpentGas;
    }
}
