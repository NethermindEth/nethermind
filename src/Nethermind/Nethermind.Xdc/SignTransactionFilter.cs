// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Xdc;
using Nethermind.Xdc.Spec;

namespace Nethermind.TxPool.Filters;

internal sealed class SignTransactionFilter(ISigner signer, IBlockTree blockTree, ISpecProvider specProvider) : IIncomingTxFilter
{
    private (long, IXdcReleaseSpec) GetSpecAndHeader()
    {
        XdcBlockHeader header = (XdcBlockHeader)blockTree.Head.Header;
        var currentHeaderNumber = header.Number + 1;
        var xdcSpec = specProvider.GetXdcSpec(currentHeaderNumber);

        return (currentHeaderNumber, xdcSpec);
    }

    private AcceptTxResult ValidateSignTransaction(Transaction tx, long headerNumber, IXdcReleaseSpec xdcSpec)
    {
        if (tx.Data.Length < 68)
        {
            return AcceptTxResult.Invalid;
        }

        UInt256 blkNumber = new UInt256(tx.Data.Span.Slice(4, 32), true);
        if (blkNumber >= headerNumber || blkNumber <= (headerNumber - (xdcSpec.EpochLength * 2)))
        {
            // Invalid block number in special transaction data
            return AcceptTxResult.Invalid;
        }

        return AcceptTxResult.Accepted;
    }

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        var (headerNumber, spec) = GetSpecAndHeader();

        if (tx.IsSpecialTransaction((IXdcReleaseSpec)specProvider.GetFinalSpec()))
        {
            if (tx.IsSignTransaction(spec) && !ValidateSignTransaction(tx, headerNumber, spec))
            {
                return AcceptTxResult.Invalid;
            }

            if (tx.SenderAddress != signer.Address)
            {
                return AcceptTxResult.Invalid;
            }
        }

        return AcceptTxResult.Accepted;
    }
}
