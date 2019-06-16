/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Services;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Subprotocols
{
    public class NdmSubprotocol : ProtocolHandlerBase, INdmPeer, INdmSubprotocol
    {
        protected readonly IDictionary<int, Action<Packet>> MessageHandlers = new Dictionary<int, Action<Packet>>();
        private int _disposedValue;
        private int _disconnectedValue;

        private readonly BlockingCollection<Request<RequestDataDeliveryReceiptMessage, DataDeliveryReceipt>>
            _receiptsRequests =
                new BlockingCollection<Request<RequestDataDeliveryReceiptMessage, DataDeliveryReceipt>>();

        private readonly BlockingCollection<Request<GetDepositApprovalsMessage, DepositApproval[]>>
            _depositApprovalsRequests =
                new BlockingCollection<Request<GetDepositApprovalsMessage, DepositApproval[]>>();

        private readonly BlockingCollection<Request<RequestEthMessage, bool>> _requestEthRequests =
            new BlockingCollection<Request<RequestEthMessage, bool>>();
        
        private readonly IEcdsa _ecdsa;
        private readonly IWallet _wallet;
        private readonly INdmFaucet _faucet;
        private readonly PublicKey _nodeId;
        private readonly IConsumerService _consumerService;
        private readonly INdmConsumerChannelManager _ndmConsumerChannelManager;
        private Address _configuredProviderAddress;
        private Address _configuredConsumerAddress;
        private readonly bool _verifySignature;
        private bool _hiReceived;
        protected override TimeSpan InitTimeout => Timeouts.NdmHi;
        public byte ProtocolVersion { get; } = 1;
        public string ProtocolCode { get; } = Protocol.Ndm;
        public int MessageIdSpaceSize { get; } = 0x1C;

        public bool HasAvailableCapability(Capability capability) => false;
        public bool HasAgreedCapability(Capability capability) => false;
        public void AddSupportedCapability(Capability capability)
        {
        }

        public event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
        public event EventHandler<ProtocolEventArgs> SubprotocolRequested
        {
            add { }
            remove { }
        }

        public PublicKey NodeId => Session.RemoteNodeId;
        public Address ConsumerAddress { get; private set; }
        public Address ProviderAddress { get; private set; }
        public bool IsConsumer => !(ConsumerAddress is null) && ConsumerAddress != Address.Zero;
        public bool IsProvider => !(ProviderAddress is null) && ProviderAddress != Address.Zero;

        public NdmSubprotocol(ISession p2PSession, INodeStatsManager nodeStatsManager,
            IMessageSerializationService serializer, ILogManager logManager, IConsumerService consumerService,
            INdmConsumerChannelManager ndmConsumerChannelManager, IEcdsa ecdsa, IWallet wallet, INdmFaucet faucet,
            PublicKey nodeId, Address configuredProviderAddress, Address configuredConsumerAddress,
            bool verifySignature = true) : base(p2PSession, nodeStatsManager, serializer, logManager)
        {
            _ecdsa = ecdsa;
            _wallet = wallet;
            _faucet = faucet;
            _nodeId = nodeId;
            _consumerService = consumerService;
            _ndmConsumerChannelManager = ndmConsumerChannelManager;
            _configuredProviderAddress = configuredProviderAddress;
            _configuredConsumerAddress = configuredConsumerAddress;
            _verifySignature = verifySignature;
            MessageHandlers = InitMessageHandlers();
        }

        private IDictionary<int, Action<Packet>> InitMessageHandlers()
            => new Dictionary<int, Action<Packet>>
            {
                [NdmMessageCode.Hi] = message => Handle(Deserialize<HiMessage>(message.Data)),
                [NdmMessageCode.DataHeaders] = message => Handle(Deserialize<DataHeadersMessage>(message.Data)),
                [NdmMessageCode.DataHeader] = message => Handle(Deserialize<DataHeaderMessage>(message.Data)),
                [NdmMessageCode.DataHeaderStateChanged] = message =>
                    Handle(Deserialize<DataHeaderStateChangedMessage>(message.Data)),
                [NdmMessageCode.DataHeaderRemoved] =
                    message => Handle(Deserialize<DataHeaderRemovedMessage>(message.Data)),
                [NdmMessageCode.DataHeaderData] = message => Handle(Deserialize<DataHeaderDataMessage>(message.Data)),
                [NdmMessageCode.SessionStarted] = message => Handle(Deserialize<SessionStartedMessage>(message.Data)),
                [NdmMessageCode.SessionFinished] = message => Handle(Deserialize<SessionFinishedMessage>(message.Data)),
                [NdmMessageCode.DataStreamEnabled] =
                    message => Handle(Deserialize<DataStreamEnabledMessage>(message.Data)),
                [NdmMessageCode.DataStreamDisabled] =
                    message => Handle(Deserialize<DataStreamDisabledMessage>(message.Data)),
                [NdmMessageCode.DataUnavailable] =
                    message => Handle(Deserialize<DataAvailabilityMessage>(message.Data)),
                [NdmMessageCode.RequestDataDeliveryReceipt] = message =>
                    Handle(Deserialize<RequestDataDeliveryReceiptMessage>(message.Data)),
                [NdmMessageCode.DataDeliveryReceipt] =
                    message => Handle(Deserialize<DataDeliveryReceiptMessage>(message.Data)),
                [NdmMessageCode.EarlyRefundTicket] =
                    message => Handle(Deserialize<EarlyRefundTicketMessage>(message.Data)),
                [NdmMessageCode.ConfirmDepositApproval] = message =>
                    Handle(Deserialize<DepositApprovalConfirmedMessage>(message.Data)),
                [NdmMessageCode.RejectDepositApproval] = message =>
                    Handle(Deserialize<DepositApprovalRejectedMessage>(message.Data)),
                [NdmMessageCode.DepositApprovals] =
                    message => Handle(Deserialize<DepositApprovalsMessage>(message.Data)),
                [NdmMessageCode.ProviderAddressChanged] = message =>
                    Handle(Deserialize<ProviderAddressChangedMessage>(message.Data)),
                [NdmMessageCode.RequestEth] = message => Handle(Deserialize<RequestEthMessage>(message.Data)),
                [NdmMessageCode.EthRequested] = message => Handle(Deserialize<EthRequestedMessage>(message.Data))
            };

        public void Init()
        {
            try
            {
                Signature signature;
                if (_verifySignature)
                {
                    if (Logger.IsInfo) Logger.Info("Signing Hi message for NDM P2P session...");
                    var hash = Keccak.Compute(_nodeId.Address.Bytes);
                    signature = _wallet.Sign(hash, _nodeId.Address);
                    if (Logger.IsInfo) Logger.Info("Signed Hi message for NDM P2P session.");
                }
                else
                {
                    signature = new Signature(1, 1, 27);
                    if (Logger.IsInfo) Logger.Info("Signing Hi message for NDM P2P was skipped.");
                }

                if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: hi");
                Send(new HiMessage(ProtocolVersion, _configuredProviderAddress, _configuredConsumerAddress, _nodeId, signature));
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
                InitiateDisconnect(DisconnectReason.NdmInvalidHiSignature, "Invalid NDM signature for Hi message.");
                throw;
            }
        }
        
        public void HandleMessage(Packet message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} {nameof(NdmSubprotocol)} " +
                $"handling a message with code {message.PacketType}.");

            if (message.PacketType == NdmMessageCode.Hi)
            {
                if (Logger.IsInfo) Logger.Info("NDM Received Hi");
            }

            if (message.PacketType != NdmMessageCode.Hi && !_hiReceived)
            {
                throw new SubprotocolException($"{Session.RemoteNodeId}" +
                                               $"No {nameof(_hiReceived)} received prior to communication.");
            }

            Logger.Warn($"GETTING MESSAGE: ndm.{message.PacketType}");
            MessageHandlers[message.PacketType](message);
        }
        
        private void Handle(HiMessage message)
        {
            if (_hiReceived)
            {
                throw new SubprotocolException($"{nameof(HiMessage)} has already been received in the past");
            }

            _hiReceived = true;
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
                InitiateDisconnect(DisconnectReason.NdmPeerAddressesNotConfigured, "Addresses not configured for NDM peer.");
                return;
            }

            if (_verifySignature)
            {
                if (Logger.IsInfo) Logger.Info("Verifying signature for NDM P2P session...");
                var hash = Keccak.Compute(message.NodeId.Bytes);
                var address = _ecdsa.RecoverPublicKey(message.Signature, hash).Address;
                if (!message.NodeId.Address.Equals(address))
                {
                    if (Logger.IsError) Logger.Error($"Invalid signature: '{message.NodeId.Address}' <> '{address}'.");
                    InitiateDisconnect(DisconnectReason.NdmInvalidHiSignature, "Invalid NDM signature for Hi message.");

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
            _consumerService.AddProviderPeer(this);
            SendGetDataHeaders();
            SendGetDepositApprovals().ContinueWith(async t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);

                    return;
                }

                await _consumerService.UpdateDepositApprovalsAsync(t.Result, message.ProviderAddress);
            });
        }

        public void InitiateDisconnect(DisconnectReason disconnectReason, string details)
        {
            if (Interlocked.Exchange(ref _disconnectedValue, 1) == 1)
            {
                return;
            }
            
            _consumerService.FinishSessionsAsync(this).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                }
            });
            Session.InitiateDisconnect(disconnectReason, details);
        }

        private void SendGetDataHeaders()
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: getdataheaders");
            Send(new GetDataHeadersMessage());
        }

        public void SendDataHeader(DataHeader dataHeader)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: dataheader");
            Send(new DataHeaderMessage(dataHeader));
        }

        public void SendDataHeaderStateChanged(Keccak headerId, DataHeaderState state)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: dataheaderstatechanged");
            Send(new DataHeaderStateChangedMessage(headerId, state));
        }
        
        private void Handle(DataHeaderStateChangedMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: dataheaderstatechanged");
            _consumerService.ChangeDataHeaderState(message.DataHeaderId, message.State);
        }

        private void Handle(DataHeaderMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: dataheader");
            _consumerService.AddDiscoveredDataHeader(message.DataHeader, this);
        }

        public void SendDataHeaderRemoved(Keccak headerId)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: dataheaderremoved");
            Send(new DataHeaderRemovedMessage(headerId));
        }

        private void Handle(DataHeaderRemovedMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: dataheaderremoved");
            _consumerService.RemoveDiscoveredDataHeader(message.DataHeaderId);
        }

        public void ChangeConsumerAddress(Address address)
        {
            if (Logger.IsInfo) Logger.Info($"Changed address for consumer: '{ConsumerAddress}' -> '{address}'.");
            var wasConsumer = IsConsumer;
            ConsumerAddress = address;
            if (wasConsumer || !IsConsumer)
            {
                return;
            }
        }

        public void ChangeProviderAddress(Address address)
        {
            if (Logger.IsInfo) Logger.Info($"Changed address for provider: '{ProviderAddress}' -> '{address}'.");
            var wasProvider = IsProvider;
            ProviderAddress = address;
            if (wasProvider || !IsProvider)
            {
                return;
            }

            _consumerService.AddProviderPeer(this);
            SendGetDataHeaders();
            SendGetDepositApprovals().ContinueWith(async t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);

                    return;
                }

                await _consumerService.UpdateDepositApprovalsAsync(t.Result, ProviderAddress);
            });
        }

        public void ChangeHostConsumerAddress(Address address)
        {
            _configuredConsumerAddress = address;
        }

        public void ChangeHostProviderAddress(Address address)
        {
            _configuredProviderAddress = address;
        }

        public void SendConsumerAddressChanged(Address consumer)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: consumeraddresschanged");
            Send(new ConsumerAddressChangedMessage(consumer));
        }

        public void SendProviderAddressChanged(Address consumer)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: provideraddresschanged");
            Send(new ProviderAddressChangedMessage(consumer));
        }

        private void Handle(ProviderAddressChangedMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: provideraddresschanged");
            _consumerService.ChangeProviderAddressAsync(this, message.Address)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    }
                });
        }

        public void SendDataHeaderData(Keccak depositId, string data, uint consumedUnits)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: dataheaderdata");
            Send(new DataHeaderDataMessage(depositId, data, consumedUnits));
        }

        public void SendSendDataRequest(DataRequest dataRequest, uint consumedUnits)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: senddatarequest");
            Send(new SendDataRequestMessage(dataRequest, consumedUnits));
        }

        public void SendEarlyRefundTicket(EarlyRefundTicket ticket, RefundReason reason)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: earlyrefundticket");
            Send(new EarlyRefundTicketMessage(ticket, reason));
        }
        
        private void Handle(EarlyRefundTicketMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: earlyrefundticket");
            _consumerService.SetEarlyRefundTicketAsync(message.Ticket, message.Reason).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                }
            });
        }

        public void SendSessionStarted(Core.Domain.Session session)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: sessionstarted");
            Send(new SessionStartedMessage(session));
        }

        private void Handle(SessionStartedMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: sessionstarted");
            _consumerService.StartSessionAsync(message.Session, this);
        }
        
        public void SendFinishSession(Keccak depositId)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: finishsession");
            Send(new FinishSessionMessage(depositId));
        }

        public void SendEnableDataStream(Keccak depositId, string[] subscriptions)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: enabledatastream");
            Send(new EnableDataStreamMessage(depositId, subscriptions));
        }
        
        public void SendDisableDataStream(Keccak depositId)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: disabledatastream");
            Send(new DisableDataStreamMessage(depositId));
        }
        
        public void SendRequestDepositApproval(Keccak headerId, string kyc)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: requestdepositapproval");
            Send(new RequestDepositApprovalMessage(headerId, kyc));
        }
        
        private void Handle(SessionFinishedMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: sessionfinished");
            _consumerService.FinishSessionAsync(message.Session, this).ContinueWith(t =>
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
            _consumerService.ConfirmDepositApprovalAsync(message.DataHeaderId).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                }
            });
        }

        public void SendDepositApprovalRejected(Keccak headerId)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: depositapprovalrejected");
            Send(new DepositApprovalRejectedMessage(headerId));
        }

        private void Handle(DepositApprovalRejectedMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: depositapprovalrejected");
            _consumerService.RejectDepositApprovalAsync(message.DataHeaderId).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                }
            });
        }

        public async Task<IReadOnlyList<DepositApproval>> SendGetDepositApprovals(Keccak dataHeaderId = null,
            bool onlyPending = false, CancellationToken? token = null)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: getdepositapprovals");
            var cancellationToken = token ?? CancellationToken.None;
            var message = new GetDepositApprovalsMessage(dataHeaderId, onlyPending);
            var request = new Request<GetDepositApprovalsMessage, DepositApproval[]>(message);
            _depositApprovalsRequests.Add(request, cancellationToken);
            Send(request.Message);
            var task = request.CompletionSource.Task;
            var firstTask = await Task.WhenAny(task, Task.Delay(Timeouts.NdmDepositApprovals, cancellationToken));
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
            var request = _depositApprovalsRequests.Take();
            request.CompletionSource.SetResult(message.DepositApprovals);
        }

        private void Handle(DataStreamEnabledMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: datastreamenabled");
            _consumerService.SetEnabledDataStreamAsync(message.DepositId, message.Subscriptions).ContinueWith(t =>
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
            _consumerService.SetDisabledDataStreamAsync(message.DepositId).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                }
            });
        }

        private void Handle(DataHeaderDataMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: dataheaderdata");
            _consumerService.SetUnitsAsync(message.DepositId, message.ConsumedUnits).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                }
            });
            _ndmConsumerChannelManager.PublishAsync(message.DepositId, message.Data).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                }
            });
        }

        private void Handle(DataHeadersMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: dataheaders");
            _consumerService.AddDiscoveredDataHeaders(message.DataHeaders, this);
        }

        public void SendDataAvailability(Keccak depositId, DataAvailability dataAvailability)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: dataavailability");
            Send(new DataAvailabilityMessage(depositId, dataAvailability));
        }

        private void Handle(DataAvailabilityMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: dataavailability");
            _consumerService.SetDataAvailabilityAsync(message.DepositId, message.DataAvailability).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                }
            });
        }

        public async Task<bool> SendRequestEth(Address address, UInt256 value, CancellationToken? token = null)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: requesteth");
            var cancellationToken = token ?? CancellationToken.None;
            var message = new RequestEthMessage(address, value);
            var request = new Request<RequestEthMessage, bool>(message);
            _requestEthRequests.Add(request, cancellationToken);
            Send(request.Message);
            var task = request.CompletionSource.Task;
            var firstTask = await Task.WhenAny(task, Task.Delay(Timeouts.NdmEthRequests, cancellationToken));
            if (firstTask.IsCanceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (firstTask != task)
            {
                throw new TimeoutException($"{Session.RemoteNodeId} Request timeout in " +
                                           $"{nameof(RequestEthMessage)}");
            }

            return task.Result;
        }
        
        private void Handle(RequestEthMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: requesteth");
            _faucet.TryRequestEthAsync(Session.RemoteHost, message.Address, message.Value).ContinueWith(t =>
            {
                if (t.IsFaulted && Logger.IsError)
                {
                    Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    return;
                }
                
                if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM sending: ethrequested");
                Send(new EthRequestedMessage(message.Address, message.Value, t.Result));
            });
        }
        
        private void Handle(EthRequestedMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: ethrequested");
            var request = _requestEthRequests.Take();
            request.CompletionSource.SetResult(message.IsSuccessful);
        }

        public async Task<DataDeliveryReceipt> SendRequestDataDeliveryReceipt(
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

        private void Handle(RequestDataDeliveryReceiptMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: requestdatadeliveryreceipt");
            _consumerService.SendDataDeliveryReceiptAsync(message.Request).ContinueWith(t =>
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

        private void Handle(DataDeliveryReceiptMessage message)
        {
            if (Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} NDM received: datadeliveryreceipt");
            var request = _receiptsRequests.Take();
            request.CompletionSource.SetResult(message.Receipt);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedValue, 1) == 1)
            {
                return;
            }

            if (IsConsumer)
            {
                _consumerService.FinishSessionsAsync(this).ContinueWith(t =>
                {
                    if (t.IsFaulted && Logger.IsError)
                    {
                        Logger.Error("There was an error within NDM subprotocol.", t.Exception);
                    }
                });

                _receiptsRequests?.CompleteAdding();
                _receiptsRequests?.Dispose();

            }

            _depositApprovalsRequests?.CompleteAdding();
            _depositApprovalsRequests?.Dispose();
        }

        private class Request<TMsg, TResult>
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