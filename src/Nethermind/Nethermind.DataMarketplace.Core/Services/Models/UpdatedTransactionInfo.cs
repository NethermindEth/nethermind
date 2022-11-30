// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Core.Services.Models
{
    public class UpdatedTransactionInfo
    {
        public UpdatedTransactionStatus Status { get; }
        public Keccak? Hash { get; }

        public UpdatedTransactionInfo(UpdatedTransactionStatus status, Keccak? hash = null)
        {
            Status = status;
            Hash = hash;
        }
    }
}
