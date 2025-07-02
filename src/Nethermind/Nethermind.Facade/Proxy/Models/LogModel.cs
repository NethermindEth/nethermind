// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Proxy.Models
{
    public class LogModel
    {
        public Address Address { get; set; }
        public Hash256 BlockHash { get; set; }
        public UInt256 BlockNumber { get; set; }
        public byte[] Data { get; set; }
        public UInt256 LogIndex { get; set; }
        public bool Removed { get; set; }
        public Hash256[] Topics { get; set; }
        public Hash256 TransactionHash { get; set; }
        public UInt256 TransactionIndex { get; set; }
    }
}
