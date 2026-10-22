// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Decides which blocks the inline capture rides along on. What counts as far enough from the tip, and
/// which forks can be captured at all, belongs above this layer, where the chain and the fork schedule live.</summary>
public interface IInlineCapturePolicy
{
    bool ShouldCapture(Block block);
}
