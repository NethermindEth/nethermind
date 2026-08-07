// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;

namespace Nethermind.Evm.Test.Tracing;

public abstract class GethLikeTracerTestsBase : VirtualMachineTestsBase
{
    /// <summary>Top-level frame clears a slot.</summary>
    protected byte[] ClearSstoreCode()
    {
        TestState.CreateAccount(Recipient, 1.Ether);
        TestState.Set(new StorageCell(Recipient, 0), new byte[] { 1 });
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
        TestState.Set(new StorageCell(TestItem.AddressC, 0), [1]);
        TestState.InsertCode(TestItem.AddressC, calleeCode, Spec);
        TestState.Commit(Spec);

        return Prepare.EvmCode
            .Call(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;
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
        TestState.Set(new StorageCell(Recipient, 0), new byte[] { 1 });
        TestState.Commit(Spec);

        return Prepare.EvmCode
            .PersistData("0x0", HexZero)
            .Call(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;
    }
}
