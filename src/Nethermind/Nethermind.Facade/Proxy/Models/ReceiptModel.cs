// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Proxy.Models
{
    public class ReceiptModel
    {
        public Keccak BlockHash { get; set; }
        public UInt256 BlockNumber { get; set; }
        public Address ContractAddress { get; set; }
        public UInt256 CumulativeGasUsed { get; set; }
        public Address From { get; set; }
        public UInt256 GasUsed { get; set; }

        public UInt256 EffectiveGasPrice { get; set; }
        public LogModel[] Logs { get; set; }
        public byte[]? LogsBloom { get; set; }
        public UInt256 Status { get; set; }
        public Address To { get; set; }
        public Keccak TransactionHash { get; set; }
        public UInt256 TransactionIndex { get; set; }
    }
}
