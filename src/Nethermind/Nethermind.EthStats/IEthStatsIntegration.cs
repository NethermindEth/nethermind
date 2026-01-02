// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.EthStats
{
    public interface IEthStatsIntegration : IDisposable
    {
        Task InitAsync();
    }
}
