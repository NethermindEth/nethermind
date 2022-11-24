// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class MakeDepositForRpc
    {
        public Keccak? DataAssetId { get; set; }
        public uint Units { get; set; }
        public UInt256 Value { get; set; }
    }
}
