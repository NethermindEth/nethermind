// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.DataMarketplace.Core.Services.Models
{
    public class NdmProxy
    {
        public bool Enabled { get; }
        public IEnumerable<string> Urls { get; }

        public NdmProxy(bool enabled, IEnumerable<string> urls)
        {
            Enabled = enabled;
            Urls = urls;
        }
    }
}
