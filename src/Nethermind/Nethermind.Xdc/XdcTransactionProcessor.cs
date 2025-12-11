// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc;

internal class XdcTransactionProcessor(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine? virtualMachine,
        ICodeInfoRepository? codeInfoRepository,
        ILogManager? logManager) : TransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
{
    protected override TransactionResult ValidateStatic(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts, in IntrinsicGas intrinsicGas)
    {
        var xdcSpec = spec as XdcReleaseSpec;
        Address target = tx.To;
        Address sender = tx.SenderAddress;

        if (xdcSpec.BlackListHFNumber <= header.Number)
        {
            if (IsBlackListed(xdcSpec, sender) || IsBlackListed(xdcSpec, target))
            {
                // Skip processing special transactions if either sender or recipient is blacklisted
                return TransactionResult.ContainsBlacklistedAddress;
            }
        }

        if (tx.IsSpecialTransaction(xdcSpec))
        {
            if (target == xdcSpec.BlockSignersAddress)
            {
                if (tx.Data.Length < 68)
                {
                    return TransactionResult.MalformedTransaction;
                }

                UInt256 blkNumber = new UInt256(tx.Data.Span.Slice(4, 32), true);
                if (blkNumber >= header.Number || blkNumber <= (header.Number - (xdcSpec.EpochLength * 2)))
                {
                    // Invalid block number in special transaction data
                    return TransactionResult.MalformedTransaction;
                }

            }
        }

        return base.ValidateStatic(tx, header, spec, opts, intrinsicGas);
    }

    private bool IsBlackListed(IXdcReleaseSpec spec, Address sender)
    {
        return spec.BlackListedAddresses.Contains(sender);
    }

}
