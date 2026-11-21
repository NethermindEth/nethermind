// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Specs;

namespace Nethermind.Evm.Test.Tracing;

public abstract class GethLikeTracerTestsBase : VirtualMachineTestsBase
{
    /// <summary>Top-level frame clears a slot.</summary>
    protected byte[] ClearSstoreCode()
    {
        TestState.CreateAccount(Recipient, 1.Ether);
        TestState.Set(new StorageCell(Recipient, 0), (UInt256)1);
        TestState.Commit(Spec);

        return Prepare.EvmCode
            .PersistData("0x0", HexZero)
            .Op(Instruction.STOP)
            .Done;
    }

    /// <summary>Child frame clears a slot and then reverts.</summary>
    protected byte[] ChildClearThenRevertCode()
    {
        byte[] calleeCode = Prepare.EvmCode
            .PersistData("0x0", HexZero)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.REVERT)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.Set(new StorageCell(TestItem.AddressC, 0), (UInt256)1);
        TestState.InsertCode(TestItem.AddressC, calleeCode, Spec);
        TestState.Commit(Spec);

        return Prepare.EvmCode
            .Call(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;
    }

    /// <summary>Top-level frame sets an empty slot, restores it to zero and sets it again.</summary>
    protected static byte[] SetClearSetSlotCode() => Prepare.EvmCode
        .PersistData("0x1", "0x1")
        .PersistData("0x1", HexZero)
        .PersistData("0x1", "0x1")
        .Op(Instruction.STOP)
        .Done;

    /// <summary>Prepares an Amsterdam transaction that runs <see cref="SetClearSetSlotCode"/>.</summary>
    /// <param name="reservoirFunded">
    /// When set, the state gas reservoir pays all but 20 gas of the first SSTORE's state gas, so the restoring SSTORE
    /// gives back to the gas left less than its execution gas cost.
    /// </param>
    protected (Block block, Transaction transaction) PrepareSetClearSetSlotTx(bool reservoirFunded) => PrepareTx(
        MainnetSpecProvider.AmsterdamActivation,
        reservoirFunded ? Eip7825Constants.DefaultTxGasLimitCap + (ulong)GasCostOf.SSetState - 20 : 500_000,
        SetClearSetSlotCode(),
        blockGasLimit: 2 * Eip7825Constants.DefaultTxGasLimitCap);

    /// <summary>Prepares a Cancun transaction whose child frame runs out of gas on an SSTORE that would clear a slot.</summary>
    /// <remarks>
    /// Cancun, not the fixture's default fork: the legacy SSTORE path does not report its cost early, so on it the
    /// attempted refund never reaches the failing step.
    /// </remarks>
    protected (Block block, Transaction transaction) PrepareChildClearOutOfGasTx()
    {
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.Set(new StorageCell(TestItem.AddressC, 0), (UInt256)1);
        TestState.InsertCode(TestItem.AddressC, Prepare.EvmCode.PersistData("0x0", HexZero).Done, Spec);
        TestState.Commit(Spec);

        byte[] code = Prepare.EvmCode
            .Call(TestItem.AddressC, 2400)
            .Op(Instruction.STOP)
            .Done;
        return PrepareTx(MainnetSpecProvider.CancunActivation, 100000UL, code);
    }

    /// <summary>Parent frame clears a slot, then calls a child that reverts.</summary>
    protected byte[] RefundThenChildRevertCode()
    {
        byte[] calleeCode = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.REVERT)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, calleeCode, Spec);

        TestState.CreateAccount(Recipient, 1.Ether);
        TestState.Set(new StorageCell(Recipient, 0), (UInt256)1);
        TestState.Commit(Spec);

        return Prepare.EvmCode
            .PersistData("0x0", HexZero)
            .Call(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;
    }

    /// <summary>Parent calls a callee with too little gas, then pops the failed call's result and stops.</summary>
    /// <param name="precompile">Whether the callee is the SHA256 precompile, rather than a contract whose fourth <c>PUSH1</c> runs out of gas.</param>
    protected byte[] CalleeOutOfGasCode(bool precompile)
    {
        Address callee = precompile ? Sha256Precompile.Address : TestItem.AddressC;
        if (!precompile)
        {
            TestState.CreateAccount(callee, 1.Ether);
            TestState.InsertCode(callee, Prepare.EvmCode.PushData(1).PushData(1).PushData(1).PushData(1).Done, Spec);
            TestState.Commit(Spec);
        }

        return Prepare.EvmCode
            .Call(callee, 10)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;
    }
}
