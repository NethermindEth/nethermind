// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Nethermind.Core;
using Nethermind.Core.Crypto;

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

            string[] enodeParts = enodeString.Split(':');
            string[] enodeParts2 = enodeParts[1].Split('@');
            _nodeKey = new PublicKey(enodeParts2[0].TrimStart('/'));
            string host = enodeParts2[1];

            if (enodeParts.Length != 3)
            {
                throw GetPortException(host);
            }

            string[] portParts = enodeParts[2].Split("?discport=");

            switch (portParts.Length)
            {
                case 1:
                    if (int.TryParse(portParts[0], out int port))
                    {
                        Port = port;
                        DiscoveryPort = port;
                    }
                    else throw GetPortException(host);
                    break;
                case 2:
                    if (int.TryParse(portParts[0], out int listeningPort) && int.TryParse(portParts[1], out int discoveryPort))
                    {
                        Port = listeningPort;
                        DiscoveryPort = discoveryPort;
                    }
                    else throw GetPortException(host);
                    break;
                default:
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
            for (var index = 0; index < hostAddresses.Length; index++)
            {
                var hostAddress = hostAddresses[index];
                if (Equals(hostAddress, hostAddress.MapToIPv4()))
                {
                    return hostAddress;
                }
            }

            return hostAddresses.FirstOrDefault()?.MapToIPv4();
        }

        public PublicKey PublicKey => _nodeKey;
        public Address Address => _nodeKey.Address;
        public IPAddress HostIp { get; }
        public int Port { get; }
        public int DiscoveryPort { get; }
        public string Info => DiscoveryPort == Port
            ? $"enode://{_nodeKey.ToString(false)}@{HostIp}:{Port}"
            : $"enode://{_nodeKey.ToString(false)}@{HostIp}:{Port}?discport={DiscoveryPort}";

        public override string ToString() => Info;
    }
}
