// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.State.Pbt.ScopeProvider;

internal static class StateHeaderProviderExtensions
{
    /// <summary>Resolves the base block a scope for <paramref name="targetBlock"/> opens at: pre-genesis for genesis, its parent otherwise.</summary>
    public static bool TryGetBaseBlock(this IStateHeaderProvider stateHeaderProvider, BlockHeader targetBlock, out BlockHeader? parent)
    {
        ArgumentNullException.ThrowIfNull(targetBlock);
        if (targetBlock.IsGenesis)
        {
            parent = null;
            return true;
        }

        parent = stateHeaderProvider.FindParentHeader(targetBlock);
        return parent is not null;
    }
}
