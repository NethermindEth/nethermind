// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Services
{
    public interface INdmFaucet
    {
        bool IsInitialized { get; }

        Task<FaucetResponse> TryRequestEthAsync(string node, Address address, UInt256 value);
    }
}
