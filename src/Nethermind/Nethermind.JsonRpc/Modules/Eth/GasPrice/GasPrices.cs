// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth.GasPrice;
public class GasPrices
{
    public UInt256 Gas { get; set; }
    public UInt256 BlobGas { get; set; }
}
