using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State;

public class OverlayStateReader : IStateReader
{
    public OverlayStateReader(StateReader stateReader, VerkleStateReader verkleStateReader)
    {
    }

    public bool TryGetAccount(Hash256 stateRoot, Address address, out AccountStruct account)
    {
        throw new NotImplementedException();
    }

    public ReadOnlySpan<byte> GetStorage(Hash256 stateRoot, Address address, in UInt256 index)
    {
        throw new NotImplementedException();
    }

    public byte[]? GetCode(Hash256 codeHash)
    {
        throw new NotImplementedException();
    }

    public byte[]? GetCode(in ValueHash256 codeHash)
    {
        throw new NotImplementedException();
    }

    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>
    {
        throw new NotImplementedException();
    }

    public bool HasStateForRoot(Hash256 stateRoot)
    {
        throw new NotImplementedException();
    }

    public Account? GetAccountDefault(Hash256 stateRoot, Address address)
    {
        throw new NotImplementedException();
    }
}