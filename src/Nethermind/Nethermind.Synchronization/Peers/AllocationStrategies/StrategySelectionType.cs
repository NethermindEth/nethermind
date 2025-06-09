// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Synchronization.Peers.AllocationStrategies;

public enum StrategySelectionType
{
    Better = 1,
    AtLeastTheSame = 0,
    CanBeSlightlyWorse = -1
}
