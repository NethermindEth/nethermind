// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin;

/// <summary>
/// Advertises post-merge eth capabilities once the node is operating post-merge —
/// i.e. the merge transition has finished or the terminal PoW block has been reached.
/// </summary>
public class MergeP2PCapabilityResolver : IP2PCapabilityResolver, IDisposable
{
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly ITxPoolConfig _txPoolConfig;

    public event Action? Changed;

    public MergeP2PCapabilityResolver(IPoSSwitcher poSSwitcher, ITxPoolConfig txPoolConfig)
    {
        _poSSwitcher = poSSwitcher;
        _txPoolConfig = txPoolConfig;
        _poSSwitcher.TerminalBlockReached += OnTerminalBlockReached;
    }

    public void Resolve(ISet<Capability> capabilities)
    {
        // TransitionFinished alone is insufficient on a live TTD transition: it only flips true later in
        // PoSSwitcher.ForkchoiceUpdated, which raises no event, so the TerminalBlockReached-driven cache
        // rebuild would still see it false. HasEverReachedTerminalBlock() (set when TerminalBlockReached
        // fires) mirrors the pre-resolver behaviour of advertising as soon as the terminal block is reached.
        if (!_poSSwitcher.TransitionFinished && !_poSSwitcher.HasEverReachedTerminalBlock()) return;

        capabilities.Add(new Capability(Protocol.Eth, 69));
        capabilities.Add(new Capability(Protocol.Eth, 70));
        capabilities.Add(new Capability(Protocol.Eth, 71));
        if (_txPoolConfig.BlobsSupport.IsEnabled())
        {
            capabilities.Add(new Capability(Protocol.Eth, 72));
        }
    }

    private void OnTerminalBlockReached(object? sender, EventArgs e) => Changed?.Invoke();

    public void Dispose() => _poSSwitcher.TerminalBlockReached -= OnTerminalBlockReached;
}
