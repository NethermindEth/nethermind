// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public readonly struct TxExecutionContext(
        in BlockExecutionContext blockExecutionContext,
        Address origin,
        in UInt256 gasPrice,
        byte[][] blobVersionedHashes,
        ICodeInfoRepository codeInfoRepository)
    {
        public readonly Address Origin = origin;
        public readonly UInt256 GasPrice = gasPrice;
        public readonly byte[][]? BlobVersionedHashes = blobVersionedHashes;
        public readonly ICodeInfoRepository CodeInfoRepository = codeInfoRepository;
        public readonly BlockExecutionContext BlockExecutionContext = blockExecutionContext;
    }
}
