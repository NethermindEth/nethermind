// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Services.Models
{
    public class CurrentGasPrice
    {
        public long GasLimit { get; set; }
        public UInt256 GasPrice { get; set; }
    }
}
