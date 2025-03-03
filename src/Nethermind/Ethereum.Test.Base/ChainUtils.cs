// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;

namespace Ethereum.Test.Base;

public static class ChainUtils
{
    private static readonly TxValidator MainnetTxValidator = new(MainnetSpecProvider.Instance.ChainId);
    private static readonly TxValidator GnosisTxValidator = new(GnosisSpecProvider.Instance.ChainId);

    public static IReleaseSpec? ResolveSpec(IReleaseSpec? spec, ulong chainId)
    {
        if (chainId != GnosisSpecProvider.Instance.ChainId)
        {
            return spec;
        }

        if (spec == Cancun.Instance)
        {
            return CancunGnosis.Instance;
        }
        if (spec == Prague.Instance)
        {
            return PragueGnosis.Instance;
        }

        return spec;
    }

    public static ValidationResult ValidateTransaction(Transaction transaction, IReleaseSpec spec)
    {
        return transaction.ChainId == GnosisSpecProvider.Instance.ChainId ?
            GnosisTxValidator.IsWellFormed(transaction, spec) : MainnetTxValidator.IsWellFormed(transaction, spec);
    }
}
