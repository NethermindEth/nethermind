// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Headers;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc;

internal class XdcHeaderStore([KeyFilter(DbNames.Headers)] IDb headerDb, [KeyFilter(DbNames.BlockNumbers)] IDb blockNumberDb, IHeaderDecoder decoder) : HeaderStore(headerDb, blockNumberDb, decoder), IXdcHeaderStore
{
}
