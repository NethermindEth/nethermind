// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal interface IXdcHeaderStore : IHeaderStore
{
    void Insert(XdcBlockHeader header) => ((IHeaderStore)this).Insert(header);
    void BulkInsert(IReadOnlyList<XdcBlockHeader> headers) => BulkInsert(headers.Cast<BlockHeader>().ToList());
    new XdcBlockHeader? Get(Hash256 blockHash, bool shouldCache, long? blockNumber = null) => ((IHeaderStore)this).Get(blockHash, shouldCache, blockNumber) as XdcBlockHeader;

    void Cache(XdcBlockHeader header) => ((IHeaderStore)this).Cache(header);
}
