// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing;

public class TransactionProcessorWithBlocklist(
    ISpecProvider? specProvider,
    IWorldState? worldState,
    IVirtualMachine? virtualMachine,
    ICodeInfoRepository? codeInfoRepository,
    ILogManager? logManager,
    HashSet<AddressAsKey> blockedAddresses)
    : TransactionProcessorBase(specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
{

    protected override TransactionResult Execute(Transaction tx, in BlockExecutionContext blCtx, ITxTracer tracer,
        ExecutionOptions opts)
    {
        BlockHeader header = blCtx.Header;

        if (blockedAddresses.Contains(tx.SenderAddress))
        {
            Logger.Error(
                $"Transaction {tx.Hash} in Block {header.Number} ({header.Hash}) has a blocked SENDER. Blocked Address: {tx.SenderAddress}. Full Sender: {tx.SenderAddress}, Recipient: {tx.To}.");
        }

        if (tx.To is not null && blockedAddresses.Contains(tx.To))
        {
            Logger.Error(
                $"Transaction {tx.Hash} in Block {header.Number} ({header.Hash}) has a blocked RECIPIENT. Blocked Address: {tx.To}. Full Sender: {tx.SenderAddress}, Recipient: {tx.To}.");
        }

        return base.Execute(tx, blCtx, tracer, opts);
    }

}
