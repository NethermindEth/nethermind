// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public interface INdmPeer : IDisposable
    {
        PublicKey NodeId { get; }
        Address? ConsumerAddress { get; }
        Address? ProviderAddress { get; }
        bool IsConsumer { get; }
        bool IsProvider { get; }
        void ChangeConsumerAddress(Address address);
        void ChangeProviderAddress(Address address);
        void ChangeHostConsumerAddress(Address address);
        void ChangeHostProviderAddress(Address address);
        void SendConsumerAddressChanged(Address consumer);

        Task<DataRequestResult> SendDataRequestAsync(DataRequest dataRequest, uint consumedUnits,
            CancellationToken? token = null);

        void SendFinishSession(Keccak depositId);
        void SendEnableDataStream(Keccak depositId, string client, string?[] args);
        void SendDisableDataStream(Keccak depositId, string client);
        void SendDataDeliveryReceipt(Keccak depositId, DataDeliveryReceipt receipt);
        Task<FaucetResponse> SendRequestEthAsync(Address address, UInt256 value, CancellationToken? token = null);
        void SendRequestDepositApproval(Keccak assetId, Address consumer, string kyc);

        Task<IReadOnlyList<DepositApproval>> SendGetDepositApprovals(Keccak? dataAssetId = null,
            bool onlyPending = false, CancellationToken? token = null);
    }
}
