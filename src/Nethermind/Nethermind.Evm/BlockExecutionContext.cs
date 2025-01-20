// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm;

public readonly struct BlockExecutionContext
{
    public BlockHeader Header { get; }
    public UInt256? BlobBaseFee { get; }

    public BlockExecutionContext(BlockHeader blockHeader)
    {
        Header = blockHeader;
    }

    public BlockExecutionContext(BlockHeader blockHeader, UInt256 forceBlobBaseFee)
    {
        Header = blockHeader;
        BlobBaseFee = forceBlobBaseFee;
    }

    public static implicit operator BlockExecutionContext(BlockHeader header) => new(header);
}
