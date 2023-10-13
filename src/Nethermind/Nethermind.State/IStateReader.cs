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
        Account? GetAccount(Commitment stateRoot, Address address);

        byte[]? GetStorage(Commitment storageRoot, in UInt256 index);

        byte[]? GetCode(Commitment codeHash);

        void RunTreeVisitor(ITreeVisitor treeVisitor, Commitment stateRoot, VisitingOptions? visitingOptions = null);
    }
}
