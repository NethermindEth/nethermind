// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class FaucetResponseForRpc
    {
        public string? Status { get; set; }
        public FaucetRequestDetailsForRpc? LatestRequest { get; set; }

        public FaucetResponseForRpc()
        {
        }

        public FaucetResponseForRpc(FaucetResponse response) : this(response.Status.ToString(),
            new FaucetRequestDetailsForRpc(response.LatestRequest))
        {
        }

        public FaucetResponseForRpc(string status, FaucetRequestDetailsForRpc latestRequest)
        {
            Status = status;
            LatestRequest = latestRequest;
        }
    }
}
