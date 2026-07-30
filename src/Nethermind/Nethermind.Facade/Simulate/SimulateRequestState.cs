// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;

namespace Nethermind.Facade.Simulate;

public class SimulateRequestState : IBlobBaseFeeOverrideProvider
{
    public bool Validate { get; set; }
    public UInt256? BlobBaseFeeOverride { get; set; }
    public ulong TotalGasLeft { get; set; }
    public ulong BlockGasLeft { get; set; }
    public bool[] TxsWithExplicitGas { get; private set; } = [];

    public void SetTxsWithExplicitGas(TransactionWithSourceDetails[] calls)
    {
        if (TxsWithExplicitGas.Length < calls.Length)
        {
            TxsWithExplicitGas = new bool[calls.Length];
        }

        for (int i = 0; i < calls.Length; i++)
        {
            TxsWithExplicitGas[i] = calls[i].HadGasLimitInRequest;
        }
    }
}
