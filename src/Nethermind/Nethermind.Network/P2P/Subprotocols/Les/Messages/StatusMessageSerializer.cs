// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class StatusMessageSerializer : IZeroMessageSerializer<StatusMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, StatusMessage message)
        {
            NettyRlpStream rlpStream = new(byteBuffer);

            #region Find Lengths
            int totalContentLength = 0;
            int protocolVersionLength = Rlp.LengthOf(StatusMessage.KeyNames.ProtocolVersion) + Rlp.LengthOf(message.ProtocolVersion);
            totalContentLength += Rlp.LengthOfSequence(protocolVersionLength);

            int networkIdLength = Rlp.LengthOf(StatusMessage.KeyNames.NetworkId) + Rlp.LengthOf(message.NetworkId);
            totalContentLength += Rlp.LengthOfSequence(networkIdLength);

            int headTdLength = Rlp.LengthOf(StatusMessage.KeyNames.TotalDifficulty) + Rlp.LengthOf(message.TotalDifficulty);
            totalContentLength += Rlp.LengthOfSequence(headTdLength);

            int headHashLength = Rlp.LengthOf(StatusMessage.KeyNames.BestHash) + Rlp.LengthOf(message.BestHash);
            totalContentLength += Rlp.LengthOfSequence(headHashLength);

            int headNumLength = Rlp.LengthOf(StatusMessage.KeyNames.HeadBlockNo) + Rlp.LengthOf(message.HeadBlockNo);
            totalContentLength += Rlp.LengthOfSequence(headNumLength);

            int genesisHashLength = Rlp.LengthOf(StatusMessage.KeyNames.GenesisHash) + Rlp.LengthOf(message.GenesisHash);
            totalContentLength += Rlp.LengthOfSequence(genesisHashLength);

            int announceTypeLength = 0;
            if (message.AnnounceType.HasValue)
            {
                announceTypeLength = Rlp.LengthOf(StatusMessage.KeyNames.AnnounceType) + Rlp.LengthOf(message.AnnounceType.Value);
                totalContentLength += Rlp.LengthOfSequence(announceTypeLength);
            }

            int serveHeadersLength = 0;
            if (message.ServeHeaders)
            {
                serveHeadersLength = Rlp.LengthOf(StatusMessage.KeyNames.ServeHeaders) + Rlp.OfEmptySequence.Length;
                totalContentLength += Rlp.LengthOfSequence(serveHeadersLength);
            }

            int serveChainSinceLength = 0;
            if (message.ServeChainSince.HasValue)
            {
                serveChainSinceLength = Rlp.LengthOf(StatusMessage.KeyNames.ServeChainSince) + Rlp.LengthOf(message.ServeChainSince.Value);
                totalContentLength += Rlp.LengthOfSequence(serveChainSinceLength);
            }

            int serveRecentChainLength = 0;
            if (message.ServeRecentChain.HasValue)
            {
                serveRecentChainLength = Rlp.LengthOf(StatusMessage.KeyNames.ServeRecentChain) + Rlp.LengthOf(message.ServeRecentChain.Value);
                totalContentLength += Rlp.LengthOfSequence(serveRecentChainLength);
            }

            int serveStateSinceLength = 0;
            if (message.ServeStateSince.HasValue)
            {
                serveStateSinceLength = Rlp.LengthOf(StatusMessage.KeyNames.ServeStateSince) + Rlp.LengthOf(message.ServeStateSince.Value);
                totalContentLength += Rlp.LengthOfSequence(serveStateSinceLength);
            }

            int serveRecentStateLength = 0;
            if (message.ServeRecentState.HasValue)
            {
                serveRecentStateLength = Rlp.LengthOf(StatusMessage.KeyNames.ServeRecentState) + Rlp.LengthOf(message.ServeRecentState.Value);
                totalContentLength += Rlp.LengthOfSequence(serveRecentStateLength);
            }

            int txRelayLength = 0;
            if (message.TxRelay)
            {
                txRelayLength = Rlp.LengthOf(StatusMessage.KeyNames.TxRelay) + Rlp.OfEmptySequence.Length;
                totalContentLength += Rlp.LengthOfSequence(txRelayLength);
            }

            int bufferLimitLength = 0;
            if (message.BufferLimit.HasValue)
            {
                bufferLimitLength = Rlp.LengthOf(StatusMessage.KeyNames.BufferLimit) + Rlp.LengthOf(message.BufferLimit.Value);
                totalContentLength += Rlp.LengthOfSequence(bufferLimitLength);
            }

            int maxRechargeRateLength = 0;
            if (message.MaximumRechargeRate.HasValue)
            {
                maxRechargeRateLength = Rlp.LengthOf(StatusMessage.KeyNames.MaximumRechargeRate) + Rlp.LengthOf(message.MaximumRechargeRate.Value);
                totalContentLength += Rlp.LengthOfSequence(maxRechargeRateLength);
            }

            int maxRequestCostsLength = 0;
            int innerCostListLength = 0;
            if (message.MaximumRequestCosts is not null)
            {
                // todo - what's the best way to do this? Calculating the length twice is definitely less than ideal.
                // Maybe build RLP for them here, and append bytes below?
                maxRequestCostsLength += Rlp.LengthOf(StatusMessage.KeyNames.MaximumRequestCosts);
                foreach (var item in message.MaximumRequestCosts)
                {
                    int costContentLength = Rlp.LengthOf(item.MessageCode) + Rlp.LengthOf(item.BaseCost) + Rlp.LengthOf(item.RequestCost);
                    innerCostListLength += Rlp.LengthOfSequence(costContentLength);
                }
                maxRequestCostsLength += Rlp.LengthOfSequence(innerCostListLength);
                totalContentLength += Rlp.LengthOfSequence(maxRequestCostsLength);
            }
            #endregion

            #region Encode Values
            int totalLength = Rlp.LengthOfSequence(totalContentLength);
            byteBuffer.EnsureWritable(totalLength);
            rlpStream.StartSequence(totalContentLength);

            rlpStream.StartSequence(protocolVersionLength);
            rlpStream.Encode(StatusMessage.KeyNames.ProtocolVersion);
            rlpStream.Encode(message.ProtocolVersion);

            rlpStream.StartSequence(networkIdLength);
            rlpStream.Encode(StatusMessage.KeyNames.NetworkId);
            rlpStream.Encode(message.NetworkId);

            rlpStream.StartSequence(headTdLength);
            rlpStream.Encode(StatusMessage.KeyNames.TotalDifficulty);
            rlpStream.Encode(message.TotalDifficulty);

            rlpStream.StartSequence(headHashLength);
            rlpStream.Encode(StatusMessage.KeyNames.BestHash);
            rlpStream.Encode(message.BestHash);

            rlpStream.StartSequence(headNumLength);
            rlpStream.Encode(StatusMessage.KeyNames.HeadBlockNo);
            rlpStream.Encode(message.HeadBlockNo);

            rlpStream.StartSequence(genesisHashLength);
            rlpStream.Encode(StatusMessage.KeyNames.GenesisHash);
            rlpStream.Encode(message.GenesisHash);

            if (message.AnnounceType.HasValue)
            {
                rlpStream.StartSequence(announceTypeLength);
                rlpStream.Encode(StatusMessage.KeyNames.AnnounceType);
                rlpStream.Encode(message.AnnounceType.Value);
            }

            if (message.ServeHeaders)
            {
                rlpStream.StartSequence(serveHeadersLength);
                rlpStream.Encode(StatusMessage.KeyNames.ServeHeaders);
                rlpStream.Encode(Rlp.OfEmptySequence);
            }

            if (message.ServeChainSince.HasValue)
            {
                rlpStream.StartSequence(serveChainSinceLength);
                rlpStream.Encode(StatusMessage.KeyNames.ServeChainSince);
                rlpStream.Encode(message.ServeChainSince.Value);
            }

            if (message.ServeRecentChain.HasValue)
            {
                rlpStream.StartSequence(serveRecentChainLength);
                rlpStream.Encode(StatusMessage.KeyNames.ServeRecentChain);
                rlpStream.Encode(message.ServeRecentChain.Value);
            }

            if (message.ServeStateSince.HasValue)
            {
                rlpStream.StartSequence(serveStateSinceLength);
                rlpStream.Encode(StatusMessage.KeyNames.ServeStateSince);
                rlpStream.Encode(message.ServeStateSince.Value);
            }

            if (message.ServeRecentState.HasValue)
            {
                rlpStream.StartSequence(serveRecentStateLength);
                rlpStream.Encode(StatusMessage.KeyNames.ServeRecentState);
                rlpStream.Encode(message.ServeRecentState.Value);
            }

            if (message.TxRelay)
            {
                rlpStream.StartSequence(txRelayLength);
                rlpStream.Encode(StatusMessage.KeyNames.TxRelay);
                rlpStream.Encode(Rlp.OfEmptySequence);
            }

            if (message.BufferLimit.HasValue)
            {
                rlpStream.StartSequence(bufferLimitLength);
                rlpStream.Encode(StatusMessage.KeyNames.BufferLimit);
                rlpStream.Encode(message.BufferLimit.Value);
            }

            if (message.MaximumRechargeRate.HasValue)
            {
                rlpStream.StartSequence(maxRechargeRateLength);
                rlpStream.Encode(StatusMessage.KeyNames.MaximumRechargeRate);
                rlpStream.Encode(message.MaximumRechargeRate.Value);
            }

            if (message.MaximumRequestCosts is not null)
            {
                rlpStream.StartSequence(maxRequestCostsLength);
                rlpStream.Encode(StatusMessage.KeyNames.MaximumRequestCosts);
                rlpStream.StartSequence(innerCostListLength);
                foreach (var item in message.MaximumRequestCosts)
                {
                    int length = Rlp.LengthOf(item.MessageCode) + Rlp.LengthOf(item.BaseCost) + Rlp.LengthOf(item.RequestCost);
                    rlpStream.StartSequence(length);
                    rlpStream.Encode(item.MessageCode);
                    rlpStream.Encode(item.BaseCost);
                    rlpStream.Encode(item.RequestCost);
                }
            }
            #endregion
        }

        public StatusMessage Deserialize(IByteBuffer byteBuffer)
        {
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }

        private static StatusMessage Deserialize(RlpStream rlpStream)
        {
            StatusMessage statusMessage = new();
            (int prefixLength, int contentLength) = rlpStream.PeekPrefixAndContentLength();
            var totalLength = contentLength;
            rlpStream.Position += prefixLength;
            var readLength = prefixLength;
            while (totalLength > readLength)
            {
                (prefixLength, contentLength) = rlpStream.PeekPrefixAndContentLength();
                readLength += prefixLength + contentLength;
                rlpStream.Position += prefixLength;
                string key = rlpStream.DecodeString();
                switch (key)
                {
                    case StatusMessage.KeyNames.ProtocolVersion:
                        statusMessage.ProtocolVersion = rlpStream.DecodeByte();
                        break;
                    case StatusMessage.KeyNames.NetworkId:
                        statusMessage.NetworkId = rlpStream.DecodeUInt256();
                        break;
                    case StatusMessage.KeyNames.TotalDifficulty:
                        statusMessage.TotalDifficulty = rlpStream.DecodeUInt256();
                        break;
                    case StatusMessage.KeyNames.BestHash:
                        statusMessage.BestHash = rlpStream.DecodeKeccak();
                        break;
                    case StatusMessage.KeyNames.HeadBlockNo:
                        statusMessage.HeadBlockNo = rlpStream.DecodeLong();
                        break;
                    case StatusMessage.KeyNames.GenesisHash:
                        statusMessage.GenesisHash = rlpStream.DecodeKeccak();
                        break;
                    case StatusMessage.KeyNames.AnnounceType:
                        statusMessage.AnnounceType = rlpStream.DecodeByte();
                        break;
                    case StatusMessage.KeyNames.ServeHeaders:
                        statusMessage.ServeHeaders = true;
                        rlpStream.SkipItem();
                        break;
                    case StatusMessage.KeyNames.ServeChainSince:
                        statusMessage.ServeChainSince = rlpStream.DecodeLong();
                        break;
                    case StatusMessage.KeyNames.ServeRecentChain:
                        statusMessage.ServeRecentChain = rlpStream.DecodeLong();
                        break;
                    case StatusMessage.KeyNames.ServeStateSince:
                        statusMessage.ServeStateSince = rlpStream.DecodeLong();
                        break;
                    case StatusMessage.KeyNames.ServeRecentState:
                        statusMessage.ServeRecentState = rlpStream.DecodeLong();
                        break;
                    case StatusMessage.KeyNames.TxRelay:
                        statusMessage.TxRelay = true;
                        rlpStream.SkipItem();
                        break;
                    case StatusMessage.KeyNames.BufferLimit:
                        statusMessage.BufferLimit = rlpStream.DecodeInt();
                        break;
                    case StatusMessage.KeyNames.MaximumRechargeRate:
                        statusMessage.MaximumRechargeRate = rlpStream.DecodeInt();
                        break;
                    case StatusMessage.KeyNames.MaximumRequestCosts:
                    // todo
                    default:
                        // Ignore unknown keys
                        rlpStream.Position = readLength;
                        break;
                }
            }

            return statusMessage;
        }
    }
}
