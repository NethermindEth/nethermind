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
        // rebuild would still see it false. HasEverReachedTerminalBlock() (set when TerminalBlockReached
        // fires) mirrors the pre-resolver behaviour of advertising as soon as the terminal block is reached.
        if (!_poSSwitcher.TransitionFinished && !_poSSwitcher.HasEverReachedTerminalBlock()) return;

        capabilities.Add(new Capability(Protocol.Eth, 69));
        capabilities.Add(new Capability(Protocol.Eth, 70));
        capabilities.Add(new Capability(Protocol.Eth, 71));
        if (_txPoolConfig.BlobsSupport.IsEnabled()
            && IsEip7594Enabled())
        {
            capabilities.Add(new Capability(Protocol.Eth, 72));
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

    private bool IsEip7594Enabled() => _specProvider is null || _specProvider.GetCurrentHeadSpec().IsEip7594Enabled;

    public void Dispose()
    {
        _poSSwitcher.TerminalBlockReached -= OnTerminalBlockReached;
        if (_chainHeadInfoProvider is not null)
        {
            _chainHeadInfoProvider.HeadChanged -= OnHeadChanged;
        }
    }
}
