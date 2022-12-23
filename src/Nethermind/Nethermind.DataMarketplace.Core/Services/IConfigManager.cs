// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Configs;

namespace Nethermind.DataMarketplace.Core.Services
{
    public interface IConfigManager
    {
        Task<NdmConfig?> GetAsync(string id);
        Task UpdateAsync(NdmConfig config);
    }
}
