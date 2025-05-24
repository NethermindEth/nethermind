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
        public readonly BlockExecutionContext BlockExecutionContext = blockExecutionContext;
        public Address Origin { get; } = origin;
        public readonly UInt256 GasPrice = gasPrice;
        public byte[][]? BlobVersionedHashes { get; } = blobVersionedHashes;
        public ICodeInfoRepository CodeInfoRepository { get; } = codeInfoRepository;
    }
}
