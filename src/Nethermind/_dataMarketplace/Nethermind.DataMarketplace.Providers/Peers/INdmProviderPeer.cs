using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Providers.Peers
{
    public interface INdmProviderPeer : INdmPeer
    {
        void SendProviderAddressChanged(Address consumer);
        void SendDataRequestResult(Keccak depositId, DataRequestResult result);
        void SendDataAssetData(Keccak depositId, string client, string data, uint consumedUnits);
        void SendInvalidData(Keccak depositId, InvalidDataReason reason);
        void SendDataAsset(DataAsset dataAsset);
        void SendDataAssetStateChanged(Keccak assetId, DataAssetState state);
        void SendDataAssetRemoved(Keccak assetId);
        void SendDataAvailability(Keccak depositId, DataAvailability dataAvailability);
        void SendEarlyRefundTicket(EarlyRefundTicket ticket, RefundReason reason);
        void SendSessionStarted(Session session);
        void SendSessionFinished(Session session);

        Task<DataDeliveryReceipt> SendRequestDataDeliveryReceiptAsync(DataDeliveryReceiptRequest receiptRequest,
            CancellationToken? token = null);

        void SendDepositApprovalConfirmed(Keccak assetId, Address consumer);
        void SendDepositApprovalRejected(Keccak assetId, Address consumer);
        void SendDepositApprovals(IReadOnlyList<DepositApproval> depositApprovals);
        void SendDataStreamDisabled(Keccak depositId, string client);
        void SendDataStreamEnabled(Keccak depositId, string client, string[] args);
        void SendGraceUnitsExceeded(Keccak depositId, uint consumedUnits, uint graceUnits);
    }
}