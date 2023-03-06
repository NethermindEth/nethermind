// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public readonly struct TxExecutionContext
    {
        public BlockHeader Header { get; }
        public Address Origin { get; }
        public byte[][]? BlobVersionedHashes { get; }
        public readonly UInt256 GasPrice;

        public TxExecutionContext(BlockHeader blockHeader, Address origin, byte[][] blobVersionedHashes, in UInt256 gasPrice)
        {
            Header = blockHeader;
            Origin = origin;
            BlobVersionedHashes = blobVersionedHashes;
            GasPrice = gasPrice;
        }
    }
}
