// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class PagedQueryBase
    {
        public int Page { get; set; } = 1;
        public int Results { get; set; } = 10;
    }
}
