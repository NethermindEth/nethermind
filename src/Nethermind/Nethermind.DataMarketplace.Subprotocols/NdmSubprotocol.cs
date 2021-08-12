//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Int256;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Wallet;
using Metrics = Nethermind.DataMarketplace.Consumers.Metrics;

namespace Nethermind.DataMarketplace.Subprotocols
{
    public class NdmSubprotocol : ProtocolHandlerBase, INdmPeer
    {
        protected readonly IDictionary<int, Action<Packet>> MessageHandlers;
        protected int DisposedValue;
        protected int DisconnectedValue;

        protected readonly BlockingCollection<Request<GetDepositApprovalsMessage, DepositApproval[]>>
            DepositApprovalsRequests = new BlockingCollection<Request<GetDepositApprovalsMessage, DepositApproval[]>>();

        protected readonly BlockingCollection<Request<RequestEthMessage, FaucetResponse>> RequestEthRequests =
            new BlockingCollection<Request<RequestEthMessage, FaucetResponse>>();

        protected readonly BlockingCollection<Request<DataRequestMessage, DataRequestResult>>
            DataRequestResultRequests = new BlockingCollection<Request<DataRequestMessage, DataRequestResult>>();

        protected readonly IEcdsa Ecdsa;
        protected readonly IWallet Wallet;
        protected readonly INdmFaucet Faucet;
        protected readonly PublicKey ConfiguredNodeId;
        protected readonly IConsumerService ConsumerService;
        protected readonly INdmConsumerChannelManager NdmConsumerChannelManager;
        protected Address? ConfiguredProviderAddress;
        protected Address? ConfiguredConsumerAddress;
        protected readonly bool VerifySignature;
        protected bool HiReceived;
        public override string Name => "ndm";
        protected override TimeSpan InitTimeout => Timeouts.NdmHi;
        public override byte ProtocolVersion => 1;
        public override string ProtocolCode => Protocol.Ndm;
        public override int MessageIdSpaceSize => 0x1F;

        public override event EventHandler<ProtocolInitializedEventArgs>? ProtocolInitialized;

        public override event EventHandler<ProtocolEventArgs> SubprotocolRequested
        {
            add { }
            remove { }
        }

        public PublicKey NodeId => Session.RemoteNodeId;
        public Address? ConsumerAddress { get; protected set; }
        public Address? ProviderAddress { get; protected set; }
        public bool IsConsumer => !(ConsumerAddress is null) && ConsumerAddress != Address.Zero;
        public bool IsProvider => !(ProviderAddress is null) && ProviderAddress != Address.Zero;

        public NdmSubprotocol(
            ISession p2PSession,
            INodeStatsManager nodeStatsManager,
            IMessageSerializationService serializer,
            ILogManager logManager,
            IConsumerService consumerService,
            INdmConsumerChannelManager ndmConsumerChannelManager,
            IEcdsa ecdsa,
            IWallet wallet,
            INdmFaucet faucet,
            PublicKey configuredNodeId,
            Address? configuredProviderAddress,
            Address? configuredConsumerAddress,
            bool verifySignature = true)
            : base(p2PSession, nodeStatsManager, serializer, logManager)
        {
            Ecdsa = ecdsa;
            Wallet = wallet;
            Faucet = faucet;
            ConfiguredNodeId = configuredNodeId;
            ConsumerService = consumerService;
            NdmConsumerChannelManager = ndmConsumerChannelManager;
            ConfiguredProviderAddress = configuredProviderAddress;
            ConfiguredConsumerAddress = configuredConsumerAddress;
            VerifySignature = verifySignature;
            MessageHandlers = InitMessageHandlers();
        }

        private IDictionary<int, Action<Packet>> InitMessageHandlers()
            => new Dictionary<int, Action<Packet>>
            {
                [NdmMessageCode.Hi] = message => Handle(Deserialize<HiMessage>(message.Data)),
                [NdmMessageCode.DataAssets] = message => Handle(Deserialize<DataAssetsMessage>(message.Data)),
                [NdmMessageCode.DataAsset] = message => Handle(Deserialize<DataAssetMessage>(message.Data)),
                [NdmMessageCode.DataAssetStateChanged] = message =>
                    Handle(Deserialize<DataAssetStateChangedMessage>(message.Data)),
                [NdmMessageCode.DataAssetRemoved] =
                    message => Handle(Deserialize<DataAssetRemovedMessage>(message.Data)),
                [NdmMessageCode.DataRequestResult] =
                    message => Handle(Deserialize<DataRequestResultMessage>(message.Data)),
                [NdmMessageCode.DataAssetData] = message => Handle(Deserialize<DataAssetDataMessage>(message.Data)),
                [NdmMessageCode.InvalidData] = message => Handle(Deserialize<InvalidDataMessage>(message.Data)),
                [NdmMessageCode.SessionStarted] = message => Handle(Deserialize<SessionStartedMessage>(message.Data)),
                [NdmMessageCode.SessionFinished] = message => Handle(Deserialize<SessionFinishedMessage>(message.Data)),
                [NdmMessageCode.DataStreamEnabled] =
                    message => Handle(Deserialize<DataStreamEnabledMessage>(message.Data)),
                [NdmMessageCode.DataStreamDisabled] =
                    message => Handle(Deserialize<DataStreamDisabledMessage>(message.Data)),
                [NdmMessageCode.DataAvailability] =
                    message => Handle(Deserialize<DataAvailabilityMessage>(message.Data)),
                [NdmMessageCode.RequestDataDeliveryReceipt] = message =>
                    Handle(Deserialize<RequestDataDeliveryReceiptMessage>(message.Data)),
                [NdmMessageCode.EarlyRefundTicket] =
                    message => Handle(Deserialize<EarlyRefundTicketMessage>(message.Data)),
                [NdmMessageCode.DepositApprovalConfirmed] = message =>
                    Handle(Deserialize<DepositApprovalConfirmedMessage>(message.Data)),
                [NdmMessageCode.DepositApprovalRejected] = message =>
                    Handle(Deserialize<DepositApprovalRejectedMessage>(message.Data)),
                [NdmMessageCode.DepositApprovals] =
                    message => Handle(Deserialize<DepositApprovalsMessage>(message.Data)),
                [NdmMessageCode.ProviderAddressChanged] = message =>
                    Handle(Deserialize<ProviderAddressChangedMessage>(message.Data)),
                [NdmMessageCode.EthRequested] = message => Handle(Deserialize<EthRequestedMessage>(message.Data)),
                [NdmMessageCode.GraceUnitsExceeded] = message => Handle(Deserialize<GraceUnitsExceededMessage>(message.Data))
            };

        public override void Init()
        {
            try
            {
                Signature signature;
                if (VerifySignature)
                {
                    if (Logger.IsInfo) Logger.Info("Signing Hi message for NDM P2P session...");
                    var hash = Keccak.Compute(ConfiguredNodeId.Address.Bytes);
                    signature = Wallet.Sign(hash, ConfiguredNodeId.Address);
                    if (Logger.IsInfo) Logger.Info("Signed Hi message for NDM P2P session.");
                }
                else
                {
                    signature = new Signature(1, 1, 27);
                    if (Logger.IsInfo) Logger.Info("Signing Hi message for NDM P2P was skipped.");
                }

                if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: hi");
                Send(new HiMessage(ProtocolVersion, ConfiguredProviderAddress, ConfiguredConsumerAddress, ConfiguredNodeId, signature));
                CheckProtocolInitTimeout().ContinueWith(x =>
                {
                    if (x.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("Error during NDM protocol handler timeout logic", x.Exception);
                    }
                });
            }
            catch (Exception ex)
            {
                if (Logger.IsError) Logger.Error(ex.ToString(), ex);
                Session.InitiateDisconnect(DisconnectReason.NdmInvalidHiSignature, "Invalid NDM signature for Hi message.");
                throw;
            }
        }

        public override void HandleMessage(Packet message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} {nameof(NdmSubprotocol)} handling a message with code {message.PacketType}.");

            if (message.PacketType == NdmMessageCode.Hi)
            {
                if (Logger.IsInfo) Logger.Info("NDM Received Hi");
            }

            if (message.PacketType != NdmMessageCode.Hi && !HiReceived)
            {
                throw new SubprotocolException($"{Session.RemoteNodeId}" +
                                               $"No {nameof(HiReceived)} received prior to communication.");
            }
            
            Logger.Warn($"GETTING MESSAGE: ndm.{NdmMessageCode.GetDescription(message.PacketType)}");

            MessageHandlers[message.PacketType](message);
        }

        protected virtual void Handle(HiMessage message)
        {
            if (HiReceived)
            {
                throw new SubprotocolException($"{nameof(HiMessage)} has already been received in the past");
            }

            HiReceived = true;
            if (Logger.IsTrace)
            {
                if (Logger.IsInfo)
                {
                    Logger.Info($"{Session.RemoteNodeId} NDM received hi with" +
                                Environment.NewLine + $" prot version\t{message.ProtocolVersion}" +
                                Environment.NewLine + $" provider address\t{message.ProviderAddress}" +
                                Environment.NewLine + $" consumer address\t{message.ConsumerAddress}" +
                                Environment.NewLine + $" node id\t{message.NodeId}");
                }
            }

            ProviderAddress = message.ProviderAddress;
            ConsumerAddress = message.ConsumerAddress;

            if (!(IsConsumer || IsProvider))
            {
                if (Logger.IsWarn) Logger.Warn("NDM peer is neither provider nor consumer (no addresses configured), skipping subprotocol connection.");
                Session.InitiateDisconnect(DisconnectReason.NdmPeerAddressesNotConfigured, "Addresses not configured for NDM peer.");
                return;
            }

            if (VerifySignature)
            {
                if (Logger.IsInfo) Logger.Info("Verifying signature for NDM P2P session...");
                var hash = Keccak.Compute(message.NodeId.Bytes);
                var address = Ecdsa.RecoverPublicKey(message.Signature, hash).Address;
                if (!message.NodeId.Address.Equals(address))
                {
                    if (Logger.IsError) Logger.Error($"Invalid signature: '{message.NodeId.Address}' <> '{address}'.");
                    Session.InitiateDisconnect(DisconnectReason.NdmInvalidHiSignature, "Invalid NDM signature for Hi message.");

                    return;
                }

                if (Logger.IsInfo) Logger.Info("NDM P2P session was verified successfully.");
            }
            else
            {
                if (Logger.IsInfo) Logger.Info("NDM P2P signature verification was skipped.");
            }

            ReceivedProtocolInitMsg(message);

            var eventArgs = new NdmProtocolInitializedEventArgs(this)
            {
                Protocol = message.Protocol,
                ProtocolVersion = message.ProtocolVersion,
                ProviderAddress = message.ProviderAddress,
                ConsumerAddress = message.ConsumerAddress
            };

            ProtocolInitialized?.Invoke(this, eventArgs);

            if (!IsProvider)
            {
                return;
            }

            ConsumerService.AddProviderPeer(this);
            SendGetDataAssets();
            SendGetDepositApprovals().ContinueWith(async t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);

                    return;
                }

                await ConsumerService.UpdateDepositApprovalsAsync(t.Result, message.ProviderAddress);
            });
        }

        public override void DisconnectProtocol(DisconnectReason disconnectReason, string details)
        {
            if (Interlocked.Exchange(ref DisconnectedValue, 1) == 1)
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
        }

        private void SendGetDataAssets()
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: getdataassets");
            Send(new GetDataAssetsMessage());
        }

        private void Handle(DataAssetStateChangedMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: dataassetstatechanged");
            ConsumerService.ChangeDataAssetState(message.DataAssetId, message.State);
        }

        private void Handle(DataAssetMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: dataasset");
            ConsumerService.AddDiscoveredDataAsset(message.DataAsset, this);
        }

        private void Handle(DataAssetRemovedMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: dataassetremoved");
            ConsumerService.RemoveDiscoveredDataAsset(message.DataAssetId);
        }

        public virtual void ChangeConsumerAddress(Address address)
        {
            if (Logger.IsInfo) Logger.Info($"Changed address for consumer: '{ConsumerAddress}' -> '{address}'.");
            var wasConsumer = IsConsumer;
            ConsumerAddress = address;
            if (wasConsumer || !IsConsumer)
            {
                return;
            }
        }

        public virtual void ChangeProviderAddress(Address address)
        {
            if (Logger.IsInfo) Logger.Info($"Changed address for provider: '{ProviderAddress}' -> '{address}'.");
            var wasProvider = IsProvider;
            ProviderAddress = address;
            if (wasProvider || !IsProvider)
            {
                return;
            }

            ConsumerService.AddProviderPeer(this);
            SendGetDataAssets();
            SendGetDepositApprovals().ContinueWith(async t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);

                    return;
                }

                await ConsumerService.UpdateDepositApprovalsAsync(t.Result, ProviderAddress);
            });
        }

        public void ChangeHostConsumerAddress(Address address)
        {
            ConfiguredConsumerAddress = address;
        }

        public void ChangeHostProviderAddress(Address address)
        {
            ConfiguredProviderAddress = address;
        }

        public void SendConsumerAddressChanged(Address consumer)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: consumeraddresschanged");
            Send(new ConsumerAddressChangedMessage(consumer));
        }

        private void Handle(ProviderAddressChangedMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: provideraddresschanged");
            ConsumerService.ChangeProviderAddressAsync(this, message.Address)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    }
                });
        }

        public async Task<DataRequestResult> SendDataRequestAsync(DataRequest dataRequest, uint consumedUnits,
            CancellationToken? token = null)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: senddatarequest");
            var cancellationToken = token ?? CancellationToken.None;
            var message = new DataRequestMessage(dataRequest, consumedUnits);
            var request = new Request<DataRequestMessage, DataRequestResult>(message);
            DataRequestResultRequests.Add(request, cancellationToken);
            Send(request.Message);
            var task = request.CompletionSource.Task;
            var firstTask = await Task.WhenAny(task, Task.Delay(Timeouts.NdmDataRequestResult, cancellationToken));
            if (firstTask.IsCanceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (firstTask != task)
            {
                throw new TimeoutException($"{Session.RemoteNodeId} Request timeout in {nameof(DataRequestMessage)}");
            }

            return task.Result;
        }

        private void Handle(DataRequestResultMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: datarequestresult");
            bool success = DataRequestResultRequests.TryTake(out var request);
            if (!success)
            {
                throw new SubprotocolException("Received a reponse for which no request has been made.");
            }

            request?.CompletionSource.SetResult(message.Result);
        }

        private void Handle(EarlyRefundTicketMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: earlyrefundticket");
            ConsumerService.SetEarlyRefundTicketAsync(message.Ticket, message.Reason).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                }
            });
        }

        private void Handle(SessionStartedMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: sessionstarted");
            ConsumerService.StartSessionAsync(message.Session, this);
        }

        public void SendFinishSession(Keccak depositId)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: finishsession");
            Send(new FinishSessionMessage(depositId));
        }

        public void SendEnableDataStream(Keccak depositId, string client, string?[] args)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: enabledatastream");
            Send(new EnableDataStreamMessage(depositId, client, args));
        }

        public void SendDisableDataStream(Keccak depositId, string client)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: disabledatastream");
            Send(new DisableDataStreamMessage(depositId, client));
        }

        public void SendRequestDepositApproval(Keccak assetId, Address consumer, string kyc)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: requestdepositapproval");
            Send(new RequestDepositApprovalMessage(assetId, consumer, kyc));
        }

        private void Handle(SessionFinishedMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: sessionfinished");
            ConsumerService.FinishSessionAsync(message.Session, this).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                }
            });
        }

        private void Handle(DepositApprovalConfirmedMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: depositapprovalconfirmed");
            ConsumerService.ConfirmDepositApprovalAsync(message.DataAssetId, message.Consumer)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    }
                });
        }

        private void Handle(DepositApprovalRejectedMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: depositapprovalrejected");
            ConsumerService.RejectDepositApprovalAsync(message.DataAssetId, message.Consumer)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    }
                });
        }

        public async Task<IReadOnlyList<DepositApproval>> SendGetDepositApprovals(Keccak? dataAssetId = null,
            bool onlyPending = false, CancellationToken? token = null)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: getdepositapprovals");
            var cancellationToken = token ?? CancellationToken.None;
            var message = new GetDepositApprovalsMessage(dataAssetId, onlyPending);
            var request = new Request<GetDepositApprovalsMessage, DepositApproval[]>(message);
            DepositApprovalsRequests.Add(request, cancellationToken);
            Send(request.Message);
            var task = request.CompletionSource.Task;
            var firstTask = await Task.WhenAny(task, Task.Delay(Timeouts.NdmDepositApproval, cancellationToken));
            if (firstTask.IsCanceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (firstTask != task)
            {
                throw new TimeoutException($"{Session.RemoteNodeId} Request timeout in " +
                                           $"{nameof(GetDepositApprovalsMessage)}");
            }

            return task.Result;
        }

        private void Handle(DepositApprovalsMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: depositapprovals");
            var success = DepositApprovalsRequests.TryTake(out var request);
            if (!success)
            {
                throw new SubprotocolException("Received a reponse for which no request has been made.");
            }

            request?.CompletionSource.SetResult(message.DepositApprovals);
        }

        private void Handle(DataStreamEnabledMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: datastreamenabled");
            ConsumerService.SetEnabledDataStreamAsync(message.DepositId, message.Client, message.Args)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    }
                });
        }

        private void Handle(DataStreamDisabledMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: datastreamdisabled");
            ConsumerService.SetDisabledDataStreamAsync(message.DepositId, message.Client)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    }
                });
        }

        private void Handle(EthRequestedMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: ethrequested");
            bool success = RequestEthRequests.TryTake(out var request);
            if (!success)
            {
                throw new SubprotocolException("Received a reponse for which no request has been made.");
            }

            request?.CompletionSource.SetResult(message.Response);
        }

        private void Handle(GraceUnitsExceededMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: graceunitsexceeded");
            ConsumerService.HandleGraceUnitsExceededAsync(message.DepositId, message.ConsumedUnits, message.GraceUnits)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    }
                });
        }

        private void Handle(DataAssetDataMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: dataassetdata");
            Metrics.ReceivedData++;
            ConsumerService.SetUnitsAsync(message.DepositId, message.ConsumedUnits).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                }
            });
            NdmConsumerChannelManager.PublishAsync(message.DepositId, message.Client, message.Data)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    }
                });
        }

        private void Handle(InvalidDataMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: invaliddata");
            ConsumerService.HandleInvalidDataAsync(message.DepositId, message.Reason).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    return;
                }

                if (Logger.IsTrace) Logger.Trace($"Received invalid data for deposit: '{message.DepositId}', reason: {message.Reason}");
            });
        }

        private void Handle(DataAssetsMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: dataassets");
            ConsumerService.AddDiscoveredDataAssets(message.DataAssets, this);
        }

        private void Handle(DataAvailabilityMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: dataavailability");
            ConsumerService.SetDataAvailabilityAsync(message.DepositId, message.DataAvailability).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                }
            });
        }

        public async Task<FaucetResponse> SendRequestEthAsync(Address address, UInt256 value, CancellationToken? token = null)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: requesteth");
            var cancellationToken = token ?? CancellationToken.None;
            var message = new RequestEthMessage(address, value);
            var request = new Request<RequestEthMessage, FaucetResponse>(message);
            RequestEthRequests.Add(request, cancellationToken);
            Send(request.Message);
            var task = request.CompletionSource.Task;
            var firstTask = await Task.WhenAny(task, Task.Delay(Timeouts.NdmEthRequest, cancellationToken));
            if (firstTask.IsCanceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (firstTask != task)
            {
                throw new TimeoutException($"{Session.RemoteNodeId} Request timeout in {nameof(RequestEthMessage)}");
            }

            return task.Result;
        }

        private void Handle(RequestDataDeliveryReceiptMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: requestdatadeliveryreceipt");
            ConsumerService.SendDataDeliveryReceiptAsync(message.Request).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                }
            });
        }

        public void SendDataDeliveryReceipt(Keccak depositId, DataDeliveryReceipt receipt)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: datadeliveryreceipt");
            Send(new DataDeliveryReceiptMessage(depositId, receipt));
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref DisposedValue, 1) == 1)
            {
                return;
            }

            try
            {
                DepositApprovalsRequests?.CompleteAdding();
                DepositApprovalsRequests?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            ConsumerService.FinishSessionsAsync(this).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                }
            });
        }

        protected class Request<TMsg, TResult>
        {
            public Request(TMsg message)
            {
                CompletionSource = new TaskCompletionSource<TResult>();
                Message = message;
            }

            public TMsg Message { get; }
            public TaskCompletionSource<TResult> CompletionSource { get; }
        }
    }
}
