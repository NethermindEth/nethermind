// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Facade.Proxy.Models
{
    public class PeerInfoModel
    {
        public string ClientId { get; set; } = null!;
        public string Host { get; set; } = null!;
        public int Port { get; set; }
        public string Address { get; set; } = null!;
        public bool IsBootnode { get; set; }
        public bool IsTrusted { get; set; }
        public bool IsStatic { get; set; }
        public string Enode { get; set; } = null!;

        // details

        public string ClientType { get; set; } = null!;
        public string EthDetails { get; set; } = null!;
        public string LastSignal { get; set; } = null!;
    }
}
