// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.Merge.Plugin;

/// <summary>
/// Advertises the post-merge eth/69, eth/70 and eth/71 capabilities once the merge transition has finished.
/// </summary>
public class MergeP2PCapabilityResolver : IP2PCapabilityResolver, IDisposable
{
    private readonly IPoSSwitcher _poSSwitcher;

    public event Action? Changed;

    public MergeP2PCapabilityResolver(IPoSSwitcher poSSwitcher)
    {
        _poSSwitcher = poSSwitcher;
        _poSSwitcher.TerminalBlockReached += OnTerminalBlockReached;
    }

    public void Resolve(ISet<Capability> capabilities)
    {
        if (!_poSSwitcher.TransitionFinished) return;

        capabilities.Add(new Capability(Protocol.Eth, 69));
        capabilities.Add(new Capability(Protocol.Eth, 70));
        capabilities.Add(new Capability(Protocol.Eth, 71));
    }

    private void OnTerminalBlockReached(object? sender, EventArgs e) => Changed?.Invoke();

    public void Dispose() => _poSSwitcher.TerminalBlockReached -= OnTerminalBlockReached;
}
