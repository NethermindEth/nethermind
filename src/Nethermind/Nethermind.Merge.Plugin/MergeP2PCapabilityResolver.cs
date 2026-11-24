// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
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
    private readonly IChainHeadSpecProvider? _specProvider;
    private readonly IChainHeadInfoProvider? _chainHeadInfoProvider;
    private bool _eip7594Enabled;

    public event Action? Changed;

    public MergeP2PCapabilityResolver(IPoSSwitcher poSSwitcher)
        : this(poSSwitcher, new TxPoolConfig { BlobsSupport = BlobsSupportMode.Disabled })
    {
    }

    public MergeP2PCapabilityResolver(IPoSSwitcher poSSwitcher, ITxPoolConfig txPoolConfig)
        : this(poSSwitcher, txPoolConfig, specProvider: null)
    {
    }

    public MergeP2PCapabilityResolver(
        IPoSSwitcher poSSwitcher,
        ITxPoolConfig txPoolConfig,
        IChainHeadSpecProvider? specProvider)
        : this(poSSwitcher, txPoolConfig, specProvider, chainHeadInfoProvider: null)
    {
    }

    public MergeP2PCapabilityResolver(
        IPoSSwitcher poSSwitcher,
        ITxPoolConfig txPoolConfig,
        IChainHeadSpecProvider? specProvider,
        IChainHeadInfoProvider? chainHeadInfoProvider)
    {
        _poSSwitcher = poSSwitcher;
        _txPoolConfig = txPoolConfig;
        _specProvider = specProvider ?? chainHeadInfoProvider?.SpecProvider;
        _chainHeadInfoProvider = chainHeadInfoProvider;
        _eip7594Enabled = IsEip7594Enabled();
        _poSSwitcher.TerminalBlockReached += OnTerminalBlockReached;
        if (_chainHeadInfoProvider is not null)
        {
            _chainHeadInfoProvider.HeadChanged += OnHeadChanged;
        }
    }

    public void Resolve(ISet<Capability> capabilities)
    {
        // TransitionFinished alone is insufficient on a live TTD transition: it only flips true later in
        // PoSSwitcher.ForkchoiceUpdated, which raises no event, so the TerminalBlockReached-driven cache
        // rebuild would still see it false. HasEverReachedTerminalBlock() also derives durable local TTD
        // evidence when no TerminalBlockReached event was raised.
        if (!_poSSwitcher.TransitionFinished && !_poSSwitcher.HasEverReachedTerminalBlock()) return;

        capabilities.Add(new Capability(Protocol.Eth, 69));
        capabilities.Add(new Capability(Protocol.Eth, 70));
        capabilities.Add(new Capability(Protocol.Eth, 71));
        // A node without blob support exchanges no cells, so before PeerDAS it can still take eth/72 as the base of eth/74 and
        // eth/75 and propagate EIP-8288 proof wrappers; otherwise a chain without EIP-7594 would never relay them.
        if (_txPoolConfig.BlobsSupport.IsEnabled()
            ? IsEip7594Enabled()
            : IsEip8288Scheduled())
        {
            capabilities.Add(new Capability(Protocol.Eth, 72));
            // EIP-8512 eth/74 adds to eth/72 the dependency proof exchange, which block sync needs once proofs leave the body.
            // EIP-8437 eth/75 extends eth/74 with proof wrapper propagation, and eth/76 extends eth/75 with aggregated (mode-1)
            // wrapper relay. All three are advertised once EIP-8288 is scheduled,
            // not active: a session keeps its negotiated version, so a peer connected before the fork would otherwise never
            // exchange wrappers. The transport ignores wrapper messages until EIP-8288 activates.
            if (IsEip8288Scheduled())
            {
                capabilities.Add(new Capability(Protocol.Eth, EthVersions.Eth74));
                capabilities.Add(new Capability(Protocol.Eth, EthVersions.Eth75));
                capabilities.Add(new Capability(Protocol.Eth, EthVersions.Eth76));
            }
        }
    }

    private void OnTerminalBlockReached(object? sender, EventArgs e) => Changed?.Invoke();

    private void OnHeadChanged(object? sender, BlockReplacementEventArgs e)
    {
        bool eip7594Enabled = IsEip7594Enabled();
        if (eip7594Enabled != _eip7594Enabled)
        {
            _eip7594Enabled = eip7594Enabled;
            Changed?.Invoke();
        }
    }

    private bool IsEip7594Enabled() => _specProvider?.GetCurrentHeadSpec().IsEip7594Enabled ?? false;

    private bool IsEip8288Scheduled() => _specProvider?.GetFinalSpec().IsEip8288Enabled ?? false;

    public void Dispose()
    {
        _poSSwitcher.TerminalBlockReached -= OnTerminalBlockReached;
        if (_chainHeadInfoProvider is not null)
        {
            _chainHeadInfoProvider.HeadChanged -= OnHeadChanged;
        }
    }
}
