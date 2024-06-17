// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using System;
using System.Collections.Generic;

namespace Nethermind.Evm
{
    public readonly struct TxExecutionContext
    {
        public readonly BlockExecutionContext BlockExecutionContext;
        public Address Origin { get; }
        public UInt256 GasPrice { get; }
        public byte[][]? BlobVersionedHashes { get; }
        public IDictionary<Address, CodeInfo> AuthorizedCode { get; }

        public TxExecutionContext(in BlockExecutionContext blockExecutionContext, Address origin, in UInt256 gasPrice, byte[][] blobVersionedHashes, IDictionary<Address, CodeInfo> authorizedCode)
        {
            BlockExecutionContext = blockExecutionContext;
            Origin = origin;
            GasPrice = gasPrice;
            BlobVersionedHashes = blobVersionedHashes;
            AuthorizedCode = authorizedCode;
        }
    }
}
