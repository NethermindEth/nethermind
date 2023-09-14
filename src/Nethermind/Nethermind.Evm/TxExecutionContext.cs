// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public readonly struct TxExecutionContext
    {
        public readonly BlockExecutionContext BlockExecutionContext;
        public Address Origin { get; }
        public UInt256 GasPrice { get; }
        public byte[][]? BlobVersionedHashes { get; }

        public TxExecutionContext(Address origin, in UInt256 gasPrice, byte[][] blobVersionedHashes, BlockExecutionContext blockExecutionContext)
        {
            Origin = origin;
            GasPrice = gasPrice;
            BlobVersionedHashes = blobVersionedHashes;
            BlockExecutionContext = blockExecutionContext;
        }
    }
}
