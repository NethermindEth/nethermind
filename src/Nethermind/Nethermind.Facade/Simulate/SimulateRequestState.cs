// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;

namespace Nethermind.Facade.Simulate;

public class SimulateRequestState : IBlobBaseFeeOverrideProvider
{
    public bool Validate { get; set; }
    public UInt256? BlobBaseFeeOverride { get; set; }
    public long TotalGasLeft { get; set; }
    public long BlockGasLeft { get; set; }
    public bool[] TxsWithExplicitGas { get; private set; } = [];

    public void SetTxsWithExplicitGas(TransactionWithSourceDetails[] calls)
    {
        if (calls.Length == 0)
        {
            TxsWithExplicitGas = [];
            return;
        }

        if (TxsWithExplicitGas.Length < calls.Length)
        {
            TxsWithExplicitGas = new bool[calls.Length];
        }

        Array.Clear(TxsWithExplicitGas, 0, calls.Length);
        for (int i = 0; i < calls.Length; i++)
        {
            TxsWithExplicitGas[i] = calls[i].HadGasLimitInRequest;
        }
    }
}
