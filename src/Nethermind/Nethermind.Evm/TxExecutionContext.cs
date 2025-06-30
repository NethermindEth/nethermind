// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public readonly struct TxExecutionContext(
        Address origin,
        ICodeInfoRepository codeInfoRepository,
        byte[][] blobVersionedHashes,
        in UInt256 gasPrice)
    {
        public readonly ValueHash256 Origin = origin.ToHash();
        public readonly ICodeInfoRepository CodeInfoRepository = codeInfoRepository;
        public readonly byte[][]? BlobVersionedHashes = blobVersionedHashes;
        public readonly UInt256 GasPrice = gasPrice;
    }
}
