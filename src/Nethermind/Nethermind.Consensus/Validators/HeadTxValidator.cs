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

public sealed class HeadTxValidator(ITxValidator strictTxValidator, ITxValidator reentranceAllowedTxValidator)
    : IHeadTxValidator, ITxFilter
{
    public static readonly HeadTxValidator Instance = new(
        new CompositeTxValidator(MaxBlobCountBlobTxValidator.Instance, GasLimitCapTxValidator.Instance),
        new CompositeTxValidator(MempoolBlobTxProofVersionValidator.Instance)
        );

    public HeadTxValidatorResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        !strictTxValidator.IsWellFormed(transaction, releaseSpec) ? HeadTxValidatorResult.Invalid :
        !reentranceAllowedTxValidator.IsWellFormed(transaction, releaseSpec) ? HeadTxValidatorResult.InvalidAllowReentrance :
        HeadTxValidatorResult.Valid;

    public AcceptTxResult IsAllowed(Transaction tx, BlockHeader _, IReleaseSpec currentSpec)
        => IsWellFormed(tx, currentSpec) is HeadTxValidatorResult.Valid ? AcceptTxResult.Accepted : AcceptTxResult.Invalid;
}
