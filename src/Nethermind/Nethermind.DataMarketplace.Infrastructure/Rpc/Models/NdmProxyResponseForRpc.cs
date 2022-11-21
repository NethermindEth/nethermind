// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class NdmProxyResponseForRpc
    {
        public bool? Enabled { get; set; }
        public IEnumerable<string>? Urls { get; set; }
    }
}
