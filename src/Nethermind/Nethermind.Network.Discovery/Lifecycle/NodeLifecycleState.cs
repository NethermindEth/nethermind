// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Lifecycle;

public enum NodeLifecycleState
{
    New,
    Active,
    ActiveWithEnr,
    EvictCandidate,
    Unreachable,
    //Active, but not included in NodeTable
    ActiveExcluded
}
