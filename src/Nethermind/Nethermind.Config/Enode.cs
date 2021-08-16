//  Copyright (c) 2021 Demerzel Solutions Limited
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

        public Enode(PublicKey nodeKey, IPAddress hostIp, int port)
        {
            _nodeKey = nodeKey;
            HostIp = hostIp;
            Port = port;
        }

        public Enode(string enodeString)
        {
            ArgumentException GetDnsException(string hostName, Exception innerException = null) =>
                new($"{hostName} is not a proper IP address nor it can be resolved by DNS.", innerException);

            ArgumentException GetPortException(string hostName) =>
                new($"Can't get Port for host {hostName}.");

            string[] enodeParts = enodeString.Split(':');
            string[] enodeParts2 = enodeParts[1].Split('@');
            _nodeKey = new PublicKey(enodeParts2[0].TrimStart('/'));
            string host = enodeParts2[1];
            if (enodeParts.Length <= 2 || !int.TryParse(enodeParts[2], out int port))
            {
                throw GetPortException(host);
                
            }
            Port = port;

            try
            {
                HostIp = IPAddress.TryParse(host, out IPAddress ip)
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
        public string Info => $"enode://{_nodeKey.ToString(false)}@{HostIp}:{Port}";

        public override string ToString() => Info;
    }
}
