// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core2.Eth1;

namespace Nethermind.Core2
{
    public interface IEth1GenesisProvider
    {
        Task<Eth1GenesisData> GetEth1GenesisDataAsync(CancellationToken cancellationToken);
    }
}
