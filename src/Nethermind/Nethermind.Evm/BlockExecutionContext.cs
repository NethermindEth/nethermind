// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm;

public readonly struct BlockExecutionContext
{
    public BlockHeader Header { get; }
    public UInt256? BlobBaseFee { get; }

    /// <summary>
    /// Extension data scoped to per block execution. Used for cached, network specific information.
    /// </summary>
    public object? ExtensionData { get; }

    public BlockExecutionContext(BlockHeader blockHeader, IReleaseSpec spec, object? extensionData = null)
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

        ExtensionData = extensionData;
    }

    public BlockExecutionContext(BlockHeader blockHeader, in UInt256 forceBlobBaseFee, object? extensionData = null)
    {
        Header = blockHeader;
        BlobBaseFee = forceBlobBaseFee;
        ExtensionData = extensionData;
    }
}
