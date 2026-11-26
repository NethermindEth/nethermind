// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Pre-EIP-6780 a SELFDESTRUCT wipes an existing contract's storage; a restore run (eth_call, estimateGas) must undo
/// that wipe like every other state change, or later runs in the same scope read the contract's storage as zeros.
/// </summary>
public class CallAndRestoreSelfDestructTests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.ShanghaiBlockTimestamp;

    [Test]
    public void CallAndRestore_of_a_self_destruct_restores_the_contract_storage()
    {
        StorageCell slot = new(Recipient, 1);
        TestState.CreateAccount(Recipient, 100.Ether);
        TestState.Set(slot, (UInt256)7);

        byte[] code = Prepare.EvmCode.PushData(Sender).Op(Instruction.SELFDESTRUCT).Done;
        (Block block, Transaction tx) = PrepareTx(Activation, 100_000, code);

        TransactionResult result = _processor.CallAndRestore(tx, new BlockExecutionContext(block.Header, Spec), NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(TestState.AccountExists(Recipient), Is.True, "the account deletion is restored");
            TestState.Get(slot, out UInt256 value);
            Assert.That(value, Is.EqualTo((UInt256)7), "the storage wipe is restored");
        }
    }
}
