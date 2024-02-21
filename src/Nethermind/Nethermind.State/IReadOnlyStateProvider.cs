// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State
{
    public interface IReadOnlyStateProvider : IAccountStateProvider
    {
        Hash256 StateRoot { get; }

        byte[]? GetCode(Address address);

        byte[]? GetCode(Hash256 codeHash);

        byte[]? GetCode(ValueHash256 codeHash);

        /// <summary>
        /// Runs a visitor over trie.
        /// </summary>
        /// <param name="visitor">Visitor to run.</param>
        /// <param name="stateRoot">Root to run on.</param>
        /// <param name="visitingOptions">Options to run visitor.</param>
        void Accept(ITreeVisitor visitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null);

        bool HasStateForRoot(Hash256 stateRoot);
    }
}
