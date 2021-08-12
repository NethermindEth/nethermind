using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Providers.Peers;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Services;
using Nethermind.DataMarketplace.Subprotocols;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Stats;
using Nethermind.Wallet;
using Session = Nethermind.DataMarketplace.Core.Domain.Session;

namespace Nethermind.DataMarketplace.Providers.Infrastructure
{
    internal class NdmProviderSubprotocol : NdmSubprotocol, INdmProviderPeer
    {
        private readonly BlockingCollection<Request<RequestDataDeliveryReceiptMessage, DataDeliveryReceipt>>
            _receiptsRequests =
                new BlockingCollection<Request<RequestDataDeliveryReceiptMessage, DataDeliveryReceipt>>();
        
        private readonly IProviderService _providerService;

        public NdmProviderSubprotocol(ISession p2PSession, INodeStatsManager nodeStatsManager,
            IMessageSerializationService serializer, ILogManager logManager, IConsumerService consumerService,
            IProviderService providerService, INdmConsumerChannelManager ndmConsumerChannelManager, IEcdsa ecdsa,
            IWallet wallet, INdmFaucet faucet, PublicKey nodeId, Address configuredProviderAddress,
            Address configuredConsumerAddress, bool verifySignature = true) : base(p2PSession, nodeStatsManager,
            serializer, logManager, consumerService, ndmConsumerChannelManager, ecdsa, wallet, faucet, nodeId,
            configuredProviderAddress, configuredConsumerAddress, verifySignature)
        {
            _providerService = providerService;
            MessageHandlers[NdmMessageCode.GetDataAssets] =
                message => Handle(Deserialize<GetDataAssetsMessage>(message.Data));
            MessageHandlers[NdmMessageCode.GetDepositApprovals] =
                message => Handle(Deserialize<GetDepositApprovalsMessage>(message.Data));
            MessageHandlers[NdmMessageCode.RequestDepositApproval] =
                message => Handle(Deserialize<RequestDepositApprovalMessage>(message.Data));
            MessageHandlers[NdmMessageCode.DataDeliveryReceipt] =
                message => Handle(Deserialize<DataDeliveryReceiptMessage>(message.Data));
            MessageHandlers[NdmMessageCode.DisableDataStream] =
                message => Handle(Deserialize<DisableDataStreamMessage>(message.Data));
            MessageHandlers[NdmMessageCode.EnableDataStream] =
                message => Handle(Deserialize<EnableDataStreamMessage>(message.Data));
            MessageHandlers[NdmMessageCode.FinishSession] =
                message => Handle(Deserialize<FinishSessionMessage>(message.Data));
            MessageHandlers[NdmMessageCode.DataRequest] =
                message => Handle(Deserialize<DataRequestMessage>(message.Data));
            MessageHandlers[NdmMessageCode.ConsumerAddressChanged] =
                message => Handle(Deserialize<ConsumerAddressChangedMessage>(message.Data));
            MessageHandlers[NdmMessageCode.RequestEth] =
                message => Handle(Deserialize<RequestEthMessage>(message.Data));
        }

        protected override void Handle(HiMessage message)
        {
            base.Handle(message);
            if (IsConsumer)
            {
                _providerService.AddConsumerPeer(this);
            }
        }
        
        private void Handle(GetDataAssetsMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: getdataassets");
            SendDataAssets();
        }
        
        private void SendDataAssets()
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: dataassets");
            _providerService.GetDataAssetsAsync(new GetDataAssets
                {
                    OnlyPublishable = true,
                    Results = int.MaxValue
                })
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("There was an error within NDM subprotocol.", t.Exception);

                        return;
                    }

                    Send(new DataAssetsMessage(t.Result.Items.ToArray()));
                });
        }

        public void SendDataRequestResult(Keccak depositId, DataRequestResult result)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: datarequestresult");
            Send(new DataRequestResultMessage(depositId, result));
        }

        public void SendDataAssetData(Keccak depositId, string client, string data, uint consumedUnits)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: dataassetdata");
            Send(new DataAssetDataMessage(depositId, client, data, consumedUnits));
        }

        public void SendInvalidData(Keccak depositId, InvalidDataReason reason)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: invaliddata");
            Send(new InvalidDataMessage(depositId, reason));
        }

        public void SendDataAsset(DataAsset dataAsset)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: dataasset");
            Send(new DataAssetMessage(dataAsset));
        }

        public void SendDataAssetStateChanged(Keccak assetId, DataAssetState state)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: dataassetstatechanged");
            Send(new DataAssetStateChangedMessage(assetId, state));
        }
        
        public void SendDataAssetRemoved(Keccak assetId)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: dataassetremoved");
            Send(new DataAssetRemovedMessage(assetId));
        }

        public void SendDataAvailability(Keccak depositId, DataAvailability dataAvailability)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: dataavailability");
            Send(new DataAvailabilityMessage(depositId, dataAvailability));
        }
        
        public void SendEarlyRefundTicket(EarlyRefundTicket ticket, RefundReason reason)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: earlyrefundticket");
            Send(new EarlyRefundTicketMessage(ticket, reason));
        }
        
        public void SendSessionStarted(Session session)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: sessionstarted");
            Send(new SessionStartedMessage(session));
        }
        
        public void SendProviderAddressChanged(Address consumer)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: provideraddresschanged");
            Send(new ProviderAddressChangedMessage(consumer));
        }

        public void SendSessionFinished(Session session)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: sessionfinished");
            Send(new SessionFinishedMessage(session));
        }
        
        public void SendDepositApprovals(IReadOnlyList<DepositApproval> depositApprovals)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: depositapprovals");
            Send(new DepositApprovalsMessage(depositApprovals.ToArray()));
        }

        public void SendDataStreamDisabled(Keccak depositId, string client)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: datastreamdisabled");
            Send(new DataStreamDisabledMessage(depositId, client));
        }

        public void SendDataStreamEnabled(Keccak depositId, string client, string[] args)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: datastreamenabled");
            Send(new DataStreamEnabledMessage(depositId, client, args));
        }

        public void SendGraceUnitsExceeded(Keccak depositId, uint consumedUnits, uint graceUnits)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: graceunitsexceeded");
            Send(new GraceUnitsExceededMessage(depositId, consumedUnits, graceUnits));
        }

        public void SendDepositApprovalConfirmed(Keccak assetId, Address consumer)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: depositapprovalconfirmed");
            Send(new DepositApprovalConfirmedMessage(assetId, consumer));
        }
        
        public void SendDepositApprovalRejected(Keccak assetId, Address consumer)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: depositapprovalrejected");
            Send(new DepositApprovalRejectedMessage(assetId, consumer));
        }
        
        public async Task<DataDeliveryReceipt> SendRequestDataDeliveryReceiptAsync(
            DataDeliveryReceiptRequest receiptRequest, CancellationToken? token = null)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: requestdatadeliveryreceipt");
            var cancellationToken = token ?? CancellationToken.None;
            var message = new RequestDataDeliveryReceiptMessage(receiptRequest);
            var request = new Request<RequestDataDeliveryReceiptMessage, DataDeliveryReceipt>(message);
            _receiptsRequests.Add(request, cancellationToken);
            Send(request.Message);
            var task = request.CompletionSource.Task;
            var firstTask = await Task.WhenAny(task, Task.Delay(Timeouts.NdmDeliveryReceipt, cancellationToken));
            if (firstTask.IsCanceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (firstTask != task)
            {
                throw new TimeoutException($"{Session.RemoteNodeId} Request timeout in " +
                                           $"{nameof(RequestDataDeliveryReceiptMessage)}");
            }

            return task.Result;
        }

        public override void ChangeConsumerAddress(Address address)
        {
            if (Logger.IsInfo) Logger.Info($"Changed address for consumer: '{ConsumerAddress}' -> '{address}'.");
            var wasConsumer = IsConsumer;
            ConsumerAddress = address;
            if (wasConsumer || !IsConsumer)
            {
                return;
            }

            _providerService.AddConsumerPeer(this);
        }

        private void Handle(DataRequestMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: datarequest");
            _providerService.StartSessionAsync(message.DataRequest, message.ConsumedUnits, this)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    }
                });
        }
        
        private void Handle(FinishSessionMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: finishsession");
            _providerService.FinishSessionAsync(message.DepositId, this).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                }
            });
        }
        
        private void Handle(RequestDepositApprovalMessage message)
        {
            if (ConsumerAddress == null)
            {
                throw new InvalidOperationException("Cannot handle request for deposit approval because the consumer address is null.");
            }
            
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: requestdepositapproval");
            _providerService.RequestDepositApprovalAsync(message.DataAssetId, ConsumerAddress, message.Kyc)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    }
                });
        }
        
        private void Handle(GetDepositApprovalsMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: getdepositapprovals");
            _providerService.SendDepositApprovalsAsync(this, message.DataAssetId, message.OnlyPending);
        }
        
        private void Handle(EnableDataStreamMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: enabledatastream");
            _providerService.EnableDataStreamAsync(message.DepositId, message.Client, (message.Args ?? Array.Empty<string>())!, this)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    }
                    
                    if (!t.Result)
                    {
                        return;
                    }
                });
        }
        
        private void Handle(DisableDataStreamMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: disabledatastream");
            _providerService.DisableDataStreamAsync(message.DepositId, message.Client, this)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    }
                    
                    if (!t.Result)
                    {
                        return;
                    }
                });
        }

        private void Handle(ConsumerAddressChangedMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: consumeraddresschanged");
            _providerService.ChangeConsumerAddressAsync(this, message.Address)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    }
                });
        }
        
        private void Handle(DataDeliveryReceiptMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: datadeliveryreceipt");
            var request = _receiptsRequests.Take();
            request.CompletionSource.SetResult(message.Receipt);
        }
        
        private void Handle(RequestEthMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: requesteth");
            Faucet.TryRequestEthAsync(Session.RemoteHost, message.Address, message.Value).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    return;
                }
                
                if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: ethrequested");
                Send(new EthRequestedMessage(t.Result));
            });
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref DisposedValue, 1) == 1)
            {
                return;
            }

            if (IsConsumer)
            {
                _providerService.FinishSessionsAsync(this).ContinueWith(t =>
                {
                    if (t.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    }
                });

                _receiptsRequests?.CompleteAdding();
                _receiptsRequests?.Dispose();

            }

            if (!IsProvider)
            {
                return;
            }

            ConsumerService.FinishSessionsAsync(this).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                }
            });
            
            DepositApprovalsRequests?.CompleteAdding();
            DepositApprovalsRequests?.Dispose();
        }
    }
}