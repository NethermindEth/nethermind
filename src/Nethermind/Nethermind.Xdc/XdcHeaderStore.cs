// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal class XdcHeaderStore([KeyFilter(DbNames.Headers)] IDb headerDb, [KeyFilter(DbNames.BlockNumbers)] IDb blockNumberDb) : HeaderStore(headerDb, blockNumberDb, new XdcHeaderDecoder()), IXdcHeaderStore
{
}
