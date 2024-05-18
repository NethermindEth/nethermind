// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State
{
    public interface IStateReader
    {
        bool TryGetAccount(Hash256 stateRoot, Address address, out AccountStruct account);
        ReadOnlySpan<byte> GetStorage(Hash256 stateRoot, Address address, in UInt256 index);
        byte[]? GetCode(Hash256 codeHash);
        byte[]? GetCode(in ValueHash256 codeHash);
        void RunTreeVisitor(ITreeVisitor treeVisitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) => RunTreeVisitor(new ContextNotAwareTreeVisitor(treeVisitor), stateRoot, visitingOptions);
        void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>;
        bool HasStateForRoot(Hash256 stateRoot);
    }
}
