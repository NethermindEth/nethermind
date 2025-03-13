// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Transition;

public class TransitionStateReader(ITrieStore trieStore, VerkleStateTree verkleState, IKeyValueStore? codeDb, ILogManager? logManager): IStateReader
{

    private readonly StateReader _merkle = new (trieStore, codeDb, logManager);
    private readonly VerkleStateReader _verkle = new (verkleState, codeDb, logManager);

    public bool TryGetAccount(Hash256 stateRoot, Address address, out AccountStruct account)
    {
        return _verkle.TryGetAccount(stateRoot, address, out account) || _merkle.TryGetAccount(stateRoot, address, out account);
    }

    public ReadOnlySpan<byte> GetStorage(Hash256 stateRoot, Address address, in UInt256 index)
    {
        ReadOnlySpan<byte> data = _verkle.GetStorage(stateRoot, address, in index);
        return data.IsEmpty ? _merkle.GetStorage(stateRoot, address, in index) : data;
    }

    public byte[]? GetCode(Hash256 codeHash)
    {
        var code = _verkle.GetCode(codeHash);
        return code ?? _merkle.GetCode(codeHash);
    }

    public byte[]? GetCode(in ValueHash256 codeHash)
    {
        var code = _verkle.GetCode(codeHash);
        return code ?? _merkle.GetCode(codeHash);
    }

    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>
    {
        throw new NotImplementedException();
    }

    public bool HasStateForRoot(Hash256 stateRoot)
    {
        return _verkle.HasStateForRoot(stateRoot) || _merkle.HasStateForRoot(stateRoot);
    }

    public Account? GetAccountDefault(Hash256 stateRoot, Address address)
    {
        Account? account = _verkle.GetAccountDefault(stateRoot, address);
        return account ?? _merkle.GetAccountDefault(stateRoot, address);
    }
}
