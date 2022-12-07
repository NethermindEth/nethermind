// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Services.Models;

namespace Nethermind.DataMarketplace.Core.Services
{
    public interface IProxyService
    {
        Task<NdmProxy?> GetAsync();
        Task SetAsync(IEnumerable<string> urls);
    }
}
