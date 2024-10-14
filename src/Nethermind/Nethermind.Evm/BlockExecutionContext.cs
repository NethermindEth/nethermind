// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm;

public readonly struct BlockExecutionContext
{
    public BlockHeader Header { get; }
    public UInt256? BlobBaseFee { get; }

    public BlockExecutionContext(BlockHeader blockHeader, IReleaseSpec spec)
    {
        Header = blockHeader;
        if (blockHeader?.ExcessBlobGas is not null)
        {
            if (!BlobGasCalculator.TryCalculateFeePerBlobGas(blockHeader.ExcessBlobGas.Value, out UInt256 feePerBlobGas, spec))
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

    // todo: maybe remove
    // for testing
    public static implicit operator BlockExecutionContext(BlockHeader header) => new(header, London.Instance);
}
