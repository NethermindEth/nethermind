// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public readonly struct TxExecutionContext
    {
        public BlockHeader Header { get; }
        public Address Origin { get; }
        public UInt256 GasPrice { get; }
        public byte[][]? BlobVersionedHashes { get; }

        public TxExecutionContext(BlockHeader blockHeader, Address origin, in UInt256 gasPrice, byte[][] blobVersionedHashes)
        {
            Header = blockHeader;
            Origin = origin;
            GasPrice = gasPrice;
            BlobVersionedHashes = blobVersionedHashes;
        }
    }
}
