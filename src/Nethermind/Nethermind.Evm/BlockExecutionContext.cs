// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm;

public struct BlockExecutionContext
{
    public BlockHeader Header { get; }
    public UInt256? BlobBaseFee { get; }

    public BlockExecutionContext(BlockHeader blockHeader, IReleaseSpec spec)
    {
        Header = blockHeader;
        if (blockHeader?.ExcessBlobGas is not null)
        {
            if (!BlobGasCalculator.TryCalculateFeePerBlobGas(blockHeader.ExcessBlobGas.Value, spec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas))
            {
                throw new OverflowException("Blob gas price calculation led to overflow.");
            }
            BlobBaseFee = feePerBlobGas;
        }
    }

    public BlockExecutionContext(BlockHeader blockHeader, UInt256 forceBlobBaseFee)
    {
        Header = blockHeader;
        BlobBaseFee = forceBlobBaseFee;
    }
}
