// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Messages;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;

namespace Nethermind.Consensus.Validators;

public sealed class HeadTxValidator : IHeadTxValidator, ITxFilter
{
    private readonly ITxValidator _strictTxValidator;
    private readonly ITxValidator _reentranceAllowedTxValidator;

    private HeadTxValidator(ITxValidator strictTxValidator, ITxValidator reentranceAllowedTxValidator)
    {
        _strictTxValidator = strictTxValidator;
        _reentranceAllowedTxValidator = reentranceAllowedTxValidator;
    }

    public static readonly HeadTxValidator Instance = new(
        new CompositeTxValidator(MaxBlobCountBlobTxValidator.Instance, GasLimitCapTxValidator.Instance),
        new CompositeTxValidator(MempoolBlobTxProofVersionValidator.Instance)
        );

    public HeadTxValidatorResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        !_strictTxValidator.IsWellFormed(transaction, releaseSpec) ? HeadTxValidatorResult.Invalid :
        !_reentranceAllowedTxValidator.IsWellFormed(transaction, releaseSpec) ? HeadTxValidatorResult.InvalidAllowReentrance :
        HeadTxValidatorResult.Valid;

    public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader, IReleaseSpec currentSpec)
        => IsWellFormed(tx, currentSpec) is HeadTxValidatorResult.Valid ? AcceptTxResult.Accepted : AcceptTxResult.Invalid;
}
