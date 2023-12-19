// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State
{
    public interface IStateReader
    {
        Account? GetAccount(Hash256 stateRoot, Address address);

        byte[]? GetStorage(Hash256 stateRoot, Address address, in UInt256 index);

        byte[]? GetCode(Hash256 codeHash);

        void RunTreeVisitor(ITreeVisitor treeVisitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null);
        bool HasStateForRoot(Hash256 stateRoot);
    }
}
