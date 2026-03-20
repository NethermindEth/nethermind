// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Headers;
using Nethermind.Core.Crypto;
using System.Collections.Generic;

namespace Nethermind.Xdc;

internal interface IXdcHeaderStore : IHeaderStore
{
    void Insert(XdcBlockHeader header) => ((IHeaderStore)this).Insert(header);
    void BulkInsert(IReadOnlyList<XdcBlockHeader> headers) => ((IHeaderStore)this).BulkInsert(headers);
    new XdcBlockHeader? Get(Hash256 blockHash, bool shouldCache, long? blockNumber = null) => ((IHeaderStore)this).Get(blockHash, shouldCache, blockNumber) as XdcBlockHeader;

    void Cache(XdcBlockHeader header) => ((IHeaderStore)this).Cache(header);
}
