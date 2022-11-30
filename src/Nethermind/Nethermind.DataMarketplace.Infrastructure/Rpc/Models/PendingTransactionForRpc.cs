// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class ResourceTransactionForRpc
    {
        public string? ResourceId { get; }
        public string? Type { get; }
        public TransactionInfoForRpc? Transaction { get; }

        public ResourceTransactionForRpc()
        {
        }

        public ResourceTransactionForRpc(ResourceTransaction transaction)
        {
            ResourceId = transaction.ResourceId;
            Type = transaction.Type;
            Transaction = new TransactionInfoForRpc(transaction.Transaction);
        }
    }
}
