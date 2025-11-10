// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Blocks;
using Nethermind.Db;

namespace Nethermind.Xdc;
internal class XdcBlockStore([KeyFilter(DbNames.Blocks)] IDb blockDb) : BlockStore(blockDb, new XdcHeaderDecoder())
{
}
