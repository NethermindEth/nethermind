// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.State;

/// <summary>Resolves the parent header of a target block.</summary>
public interface IParentHeaderProvider
{
    /// <summary>Finds the parent header of <paramref name="target"/>, or <c>null</c> when it is unavailable.</summary>
    BlockHeader? FindParentHeader(BlockHeader target);
}
