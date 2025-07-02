// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State
{
    public interface IReadOnlyStateProvider : IAccountStateProvider
    {
        Hash256 StateRoot { get; }

        byte[]? GetCode(Address address);

        byte[]? GetCode(in ValueHash256 codeHash);

        public bool IsContract(Address address);

        /// <summary>
        /// Runs a visitor over trie.
        /// </summary>
        /// <param name="visitor">Visitor to run.</param>
        /// <param name="stateRoot">Root to run on.</param>
        /// <param name="visitingOptions">Options to run visitor.</param>
        void Accept<TCtx>(ITreeVisitor<TCtx> visitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>;

        bool AccountExists(Address address);

        bool IsDeadAccount(Address address);

        bool IsEmptyAccount(Address address);

        bool HasStateForRoot(Hash256 stateRoot);
    }
}
