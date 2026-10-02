// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

/// <summary>Advertises proof gossip only after the prototype fork is active.</summary>
public sealed class LeanP2PCapabilityResolver : IP2PCapabilityResolver, IDisposable
{
    private readonly IBlockTree _blockTree;
    private readonly ISpecProvider _specProvider;
    private bool _enabled;
    public event Action? Changed;

    public LeanP2PCapabilityResolver(IBlockTree blockTree, ISpecProvider specProvider)
    {
        _blockTree = blockTree;
        _specProvider = specProvider;
        _enabled = IsEnabled();
        _blockTree.NewHeadBlock += OnHead;
    }

    public void Resolve(ISet<Capability> capabilities)
    {
        if (Volatile.Read(ref _enabled)) capabilities.Add(new Capability(LeanProtocolHandler.Code, LeanProtocolHandler.Version));
    }

    private bool IsEnabled() => _blockTree.Head is { } head && _specProvider.GetSpec(head.Header).IsEip8288Enabled;

    private void OnHead(object? sender, BlockEventArgs args)
    {
        bool enabled = IsEnabled();
        if (_enabled == enabled) return;
        Volatile.Write(ref _enabled, enabled);
        Changed?.Invoke();
    }

    public void Dispose() => _blockTree.NewHeadBlock -= OnHead;
}
