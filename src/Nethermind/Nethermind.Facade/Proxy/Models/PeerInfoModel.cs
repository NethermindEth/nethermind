// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Facade.Proxy.Models
{
    public class PeerInfoModel
    {
        public string ClientId { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Address { get; set; }
        public bool IsBootnode { get; set; }
        public bool IsTrusted { get; set; }
        public bool IsStatic { get; set; }
        public string Enode { get; set; }

        // details

        public string ClientType { get; set; }
        public string EthDetails { get; set; }
        public string LastSignal { get; set; }
    }
}
