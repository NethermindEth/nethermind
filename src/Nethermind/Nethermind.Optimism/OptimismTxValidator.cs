// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Consensus.Validators;

namespace Nethermind.Optimism;

public sealed class OptimismTxValidator : ITxValidator
{
    public static OptimismTxValidator Instance = new();
    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) => ValidationResult.Success;
}
