// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Stats.Model
{
    public enum CompatibilityValidationType
    {
        P2PVersion,
        Capabilities,
        NetworkId,
        DifferentGenesis,
        MissingForkId,
        InvalidForkId
    }
}
