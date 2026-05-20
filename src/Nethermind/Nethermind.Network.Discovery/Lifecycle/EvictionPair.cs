// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Lifecycle;

public class EvictionPair(INodeLifecycleManager evictionCandidate, INodeLifecycleManager replacementCandidate)
{
    public INodeLifecycleManager EvictionCandidate { get; init; } = evictionCandidate;
    public INodeLifecycleManager ReplacementCandidate { get; init; } = replacementCandidate;
}
