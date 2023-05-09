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
        Account? GetAccount(Keccak stateRoot, Address address);

        UInt256 GetStorage(Keccak storageRoot, in UInt256 index);

        byte[]? GetCode(Keccak codeHash);

        void RunTreeVisitor(ITreeVisitor treeVisitor, Keccak stateRoot, VisitingOptions? visitingOptions = null);
    }
}
