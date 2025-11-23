// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.TransactionProcessing;

public readonly record struct GasConsumed(ulong SpentGas, ulong OperationGas)
{
    public static implicit operator ulong(GasConsumed gas) => gas.SpentGas;
    public static implicit operator GasConsumed(ulong spentGas) => new(spentGas, spentGas);
}
