// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

public class FlatWorldStateManager : IWorldStateManager
{
    private readonly IFlatDiffRepository _flatDiffRepository;
    private readonly FlatStateReader _flatStateReader;

    public FlatWorldStateManager(
        IFlatDiffRepository flatDiffRepository,
        FlatStateReader flatStateReader
    )
    {
        _flatDiffRepository = flatDiffRepository;
        _flatStateReader = flatStateReader;
    }

    public IWorldStateScopeProvider GlobalWorldState { get; }
    public IStateReader GlobalStateReader => _flatStateReader;
    public ISnapServer? SnapServer => null;
    public IReadOnlyKeyValueStore? HashServer => null;
    public IWorldStateScopeProvider CreateResettableWorldState()
    {
        throw new NotImplementedException();
    }

    event EventHandler<ReorgBoundaryReached>? IWorldStateManager.ReorgBoundaryReached
    {
        add => _flatDiffRepository.ReorgBoundaryReached += value;
        remove => _flatDiffRepository.ReorgBoundaryReached -= value;
    }

    public IOverridableWorldScope CreateOverridableWorldScope()
    {
        throw new NotImplementedException();
    }

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("Verify trie not implemented");
        return false;
    }

    public void FlushCache(CancellationToken cancellationToken)
    {
        _flatDiffRepository.FlushCache(cancellationToken);
    }
}
