// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Services.Models;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class UpdatedTransactionInfoForRpc
    {
        public string? Status { get; set; }
        public Keccak? Hash { get; set; }

        public UpdatedTransactionInfoForRpc()
        {
        }

        public UpdatedTransactionInfoForRpc(UpdatedTransactionInfo info)
            : this(info.Status.ToString().ToLowerInvariant(), info.Hash)
        {
        }

        public UpdatedTransactionInfoForRpc(string status, Keccak? hash)
        {
            Status = status;
            Hash = hash;
        }
    }
}
