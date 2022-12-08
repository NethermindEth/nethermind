// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Initializers
{
    public class EmptyFaucet : INdmFaucet
    {
        private EmptyFaucet()
        {
        }

        public static EmptyFaucet Instance { get; } = new EmptyFaucet();

        private static readonly FaucetResponse Response = new FaucetResponse(FaucetRequestStatus.FaucetDisabled);

        public bool IsInitialized => true;

        public Task<FaucetResponse> TryRequestEthAsync(string node, Address address, UInt256 value)
            => Task.FromResult(Response);
    }
}
