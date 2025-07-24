// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.TransactionProcessing;

public readonly record struct GasConsumed(long SpentGas, long OperationGas)
{
    public static implicit operator long(GasConsumed gas) => gas.SpentGas;
    public static implicit operator GasConsumed(long spentGas) => new(spentGas, spentGas);
}
