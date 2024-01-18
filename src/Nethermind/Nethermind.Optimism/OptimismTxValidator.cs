// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class OptimismTxValidator : ITxValidator
{
    private readonly ITxValidator _txValidator;

    public OptimismTxValidator(ITxValidator txValidator)
    {
        _txValidator = txValidator;
    }

    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        IsWellFormed(transaction, releaseSpec, out _);
    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, out string? error)
    {
        error = null;
        return transaction.Type == TxType.DepositTx || _txValidator.IsWellFormed(transaction, releaseSpec, out error);
    }
    
}
