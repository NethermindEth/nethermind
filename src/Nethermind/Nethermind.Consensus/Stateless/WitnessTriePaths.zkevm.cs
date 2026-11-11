// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Stateless;

internal sealed partial class WitnessTriePaths
{
    /// <summary>Whether the walks run: only on the Airbender guest, where their cost was measured.</summary>
    private static bool Walks => AirbenderFlag.IsActive;
}
