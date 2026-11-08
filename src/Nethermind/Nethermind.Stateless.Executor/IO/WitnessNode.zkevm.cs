// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Stateless.Execution.IO;

public readonly partial struct WitnessNode
{
    /// <remarks>Only where <see cref="FirstTouchMemoryFlag"/> is on: elsewhere no node is placed, and the checks a
    /// decoded node would pass through cost every node.</remarks>
    private static partial bool CanPlaceInInput => FirstTouchMemoryFlag.IsActive;
}
