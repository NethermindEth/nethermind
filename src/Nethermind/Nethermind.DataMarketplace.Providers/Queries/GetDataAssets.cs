// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Providers.Queries
{
    public class GetDataAssets : PagedQueryBase
    {
        public bool OnlyPublishable { get; set; }
    }
}
