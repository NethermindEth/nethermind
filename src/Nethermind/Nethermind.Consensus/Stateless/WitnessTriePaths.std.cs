// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Stateless;

internal sealed partial class WitnessTriePaths
{
    /// <summary>Whether the walks run; always on the host.</summary>
    private static bool Walks => true;
}
