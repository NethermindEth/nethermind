// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public sealed class OptimismTxValidator : ITxValidator
{
    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, [NotNullWhen(false)] out string? error)
    {
        error = null;
        return true;
    }
}
