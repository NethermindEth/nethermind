// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network
{
    public sealed class NetworkNodeDecoder : RlpDecoder<NetworkNode>
    {
        public static NetworkNodeDecoder Instance { get; } = new();

        private static readonly RlpLimit RlpLimit = RlpLimit.For<NetworkNode>((int)1.KiB, nameof(NetworkNode.HostIp));

        protected override NetworkNode DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            RlpReader fields = decoderContext.EnterSequence(out int itemLength);
            ReadOnlySpan<byte> firstItem = fields.DecodeByteArraySpan(RlpLimit);
            NetworkNode node = IsNodeString(firstItem)
                ? DecodeNodeStringFormat(ref fields, firstItem)
                : DecodeLegacyFormat(ref fields, firstItem);
            // The legacy format never checked its end.
            decoderContext.ExitSequenceIgnoringRest(fields, itemLength);
            return node;
        }

        private static NetworkNode DecodeNodeStringFormat(ref RlpReader decoderContext, ReadOnlySpan<byte> firstItem)
        {
            string nodeString = Encoding.UTF8.GetString(firstItem);
            long reputation = decoderContext.DecodeLong();
            decoderContext.CheckEnd();
            NetworkNode node = new(NormalizeLegacyUnbracketedIpv6Enode(nodeString));
            node.Reputation = reputation;
            return node;
        }

        private static string NormalizeLegacyUnbracketedIpv6Enode(string nodeString)
        {
            if (!nodeString.StartsWith("enode://", StringComparison.OrdinalIgnoreCase))
            {
                return nodeString;
            }

            int hostSeparator = nodeString.IndexOf('@');
            if (hostSeparator < 0)
            {
                return nodeString;
            }

            int hostStart = hostSeparator + 1;
            int queryStart = nodeString.IndexOf('?', hostStart);
            int endpointEnd = queryStart < 0 ? nodeString.Length : queryStart;
            // The persisted legacy format always ends its endpoint with :port, including unbracketed IPv6 hosts.
            int portSeparator = nodeString.LastIndexOf(':', endpointEnd - 1);
            if (portSeparator <= hostStart)
            {
                return nodeString;
            }

            ReadOnlySpan<char> hostText = nodeString.AsSpan(hostStart, portSeparator - hostStart);
            if (hostText[0] == '['
                || !IPAddress.TryParse(hostText, out IPAddress? host)
                || host.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return nodeString;
            }

            return string.Concat(
                nodeString.AsSpan(0, hostStart),
                Enode.FormatEnodeHost(host),
                nodeString.AsSpan(portSeparator));
        }

        private static NetworkNode DecodeLegacyFormat(ref RlpReader decoderContext, ReadOnlySpan<byte> publicKeyBytes)
        {
            PublicKey publicKey = new(publicKeyBytes);
            string ip = decoderContext.DecodeString(RlpLimit);
            int port = (int)decoderContext.DecodeByteArraySpan(RlpLimit.L8).ReadEthUInt64();
            decoderContext.SkipItem();
            long reputation = 0L;
            try
            {
                reputation = decoderContext.DecodeLong();
            }
            catch (RlpReaderException)
            {
                // regression - old format
            }

            NetworkNode networkNode = new(publicKey, ip != string.Empty ? ip : null, port, reputation);
            return networkNode;
        }

        public override void Encode(ref RlpWriter writer, NetworkNode item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int contentLength = GetContentLength(item, rlpBehaviors);
            RlpSequence sequence = writer.StartSequence(contentLength);
            if (!ShouldEncodeNodeString(item))
            {
                EncodeLegacyFormat(ref writer, item);
            }
            else
            {
                writer.Encode(item.ToString());
                writer.Encode(item.Reputation);
            }

            writer.EndSequence(sequence);
        }

        public override int GetLength(NetworkNode item, RlpBehaviors rlpBehaviors) => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

        private static void EncodeLegacyFormat(ref RlpWriter writer, NetworkNode item)
        {
            writer.Encode(item.NodeId.Bytes);
            writer.Encode(item.Host);
            writer.Encode(item.Port);
            writer.Encode(string.Empty);
            writer.Encode(item.Reputation);
        }

        private static int GetContentLength(NetworkNode item, RlpBehaviors rlpBehaviors) => ShouldEncodeNodeString(item)
            ? Rlp.LengthOf(item.ToString())
                + Rlp.LengthOf(item.Reputation)
            : Rlp.LengthOf(item.NodeId.Bytes)
                + Rlp.LengthOf(item.Host)
                + Rlp.LengthOf(item.Port)
                + 1
                + Rlp.LengthOf(item.Reputation);

        private static bool ShouldEncodeNodeString(NetworkNode item) => item.IsEnr || item.DiscoveryPort != item.Port;

        private static bool IsNodeString(ReadOnlySpan<byte> value) =>
            value.Length != PublicKey.LengthInBytes &&
            (value.StartsWith("enr:"u8) || value.StartsWith("enode:"u8));
    }
}
