// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using Nethermind.Core;
using Nethermind.Core.Crypto;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Nethermind.Config
{
    public class Enode : IEnode
    {
        private readonly PublicKey _nodeKey;

        public Enode(PublicKey nodeKey, IPAddress hostIp, int port, int? discoveryPort = null)
        {
            _nodeKey = nodeKey;
            HostIp = hostIp;
            Port = port;
            DiscoveryPort = discoveryPort ?? port;
        }

        public Enode(string enodeString)
        {
            ArgumentException GetDnsException(string hostName, Exception? innerException = null) =>
                new($"{hostName} is not a proper IP address nor it can be resolved by DNS.", innerException);

            ArgumentException GetPortException(string hostName) =>
                new($"Can't get Port for host {hostName}.");

            if (!IsEnode(enodeString, out Uri? parsed))
            {
                throw new ArgumentException($"Invalid enode value '{enodeString}'");
            }

            _nodeKey = new PublicKey(parsed.UserInfo);
            string host = TrimIpV6Brackets(parsed.Host);

            if (parsed.Port == -1)
            {
                throw GetPortException(host);
            }

            Port = parsed.Port;
            if (parsed.Query.Length == 0)
            {
                DiscoveryPort = Port;
            }
            else if (parsed.Query.StartsWith("?discport=", StringComparison.Ordinal) &&
                     int.TryParse(parsed.Query["?discport=".Length..], out int discoveryPort))
            {
                DiscoveryPort = discoveryPort;
            }
            else
            {
                throw GetPortException(host);
            }

            try
            {
                HostIp = IPAddress.TryParse(host, out IPAddress? ip)
                    ? ip
                    : GetHostIpFromDnsAddresses(Dns.GetHostAddresses(host)) ?? throw GetDnsException(host);
            }
            catch (SocketException e)
            {
                throw GetDnsException(host, e);
            }
        }

        public static IPAddress? GetHostIpFromDnsAddresses(params IPAddress[] hostAddresses)
        {
            IPAddress? mappedIpv4 = null;
            for (int index = 0; index < hostAddresses.Length; index++)
            {
                IPAddress hostAddress = hostAddresses[index];
                if (hostAddress.AddressFamily == AddressFamily.InterNetwork)
                {
                    return hostAddress;
                }

                if (hostAddress.IsIPv4MappedToIPv6 && mappedIpv4 is null)
                {
                    mappedIpv4 = hostAddress.MapToIPv4();
                }
            }

            return mappedIpv4 ?? (hostAddresses.Length == 0 ? null : hostAddresses[0]);
        }

        public PublicKey PublicKey => _nodeKey;
        public Address Address => _nodeKey.Address;
        public IPAddress HostIp { get; }
        public int Port { get; }
        public int DiscoveryPort { get; }
        public string Info => DiscoveryPort == Port
            ? $"enode://{_nodeKey.ToString(false)}@{FormattedHost}:{Port}"
            : $"enode://{_nodeKey.ToString(false)}@{FormattedHost}:{Port}?discport={DiscoveryPort}";

        public override string ToString() => Info;

        private string FormattedHost
        {
            get
            {
                IPAddress hostIp = HostIp.IsIPv4MappedToIPv6 ? HostIp.MapToIPv4() : HostIp;
                return hostIp.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{hostIp}]" : hostIp.ToString();
            }
        }

        public static bool IsEnode(string enodeString, [NotNullWhen(true)] out Uri? parsed) =>
            Uri.TryCreate(enodeString, new UriCreationOptions(), out parsed) && parsed.Scheme.Equals("enode", StringComparison.OrdinalIgnoreCase);

        private static string TrimIpV6Brackets(string host) =>
            host.Length > 1 && host[0] == '[' && host[^1] == ']'
                ? host[1..^1]
                : host;
    }
}
