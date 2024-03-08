// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.TxPool
{
    public interface ITxValidator
    {
        bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, [NotNullWhen(false)] out string? error);
    }
}
