// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Lifecycle;

public class EvictionPair
{
    public EvictionPair(INodeLifecycleManager evictionCandidate, INodeLifecycleManager replacementCandidate)
    {
        EvictionCandidate = evictionCandidate;
        ReplacementCandidate = replacementCandidate;
    }

    public INodeLifecycleManager EvictionCandidate { get; init; }
    public INodeLifecycleManager ReplacementCandidate { get; init; }
}
