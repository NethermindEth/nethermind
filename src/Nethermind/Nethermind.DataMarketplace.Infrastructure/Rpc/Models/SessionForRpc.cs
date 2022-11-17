// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class SessionForRpc
    {
        public Keccak? Id { get; set; }
        public Keccak? DepositId { get; set; }
        public Keccak? DataAssetId { get; set; }
        public Address? ConsumerAddress { get; set; }
        public PublicKey? ConsumerNodeId { get; set; }
        public Address? ProviderAddress { get; set; }
        public PublicKey? ProviderNodeId { get; set; }
        public string? State { get; set; }
        public uint? StartUnitsFromConsumer { get; set; }
        public uint? StartUnitsFromProvider { get; set; }
        public ulong? StartTimestamp { get; set; }
        public ulong? FinishTimestamp { get; set; }
        public uint? ConsumedUnits { get; set; }
        public uint? UnpaidUnits { get; set; }
        public uint? PaidUnits { get; set; }
        public uint? SettledUnits { get; set; }

        public SessionForRpc()
        {
        }

        public SessionForRpc(Session session)
        {
            Id = session.Id;
            DepositId = session.DepositId;
            DataAssetId = session.DataAssetId;
            ConsumerAddress = session.ConsumerAddress;
            ConsumerNodeId = session.ConsumerNodeId;
            ProviderAddress = session.ProviderAddress;
            ProviderNodeId = session.ProviderNodeId;
            State = session.State.ToString().ToLowerInvariant();
            StartUnitsFromProvider = session.StartUnitsFromProvider;
            StartUnitsFromConsumer = session.StartUnitsFromConsumer;
            StartTimestamp = session.StartTimestamp;
            FinishTimestamp = session.FinishTimestamp;
            ConsumedUnits = session.ConsumedUnits;
            UnpaidUnits = session.UnpaidUnits;
            PaidUnits = session.PaidUnits;
            SettledUnits = session.SettledUnits;
        }
    }
}
