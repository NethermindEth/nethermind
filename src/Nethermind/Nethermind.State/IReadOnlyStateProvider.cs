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

        UInt256 GetNonce(Address address);

        UInt256 GetBalance(Address address);

        ValueHash256 GetStorageRoot(Address address);

        byte[]? GetCode(Address address);

        byte[]? GetCode(Hash256 codeHash);

        byte[]? GetCode(ValueHash256 codeHash);

        ValueHash256 GetCodeHash(Address address);

        public bool IsContract(Address address);

        /// <summary>
        /// Runs a visitor over trie.
        /// </summary>
        /// <param name="visitor">Visitor to run.</param>
        /// <param name="stateRoot">Root to run on.</param>
        /// <param name="visitingOptions">Options to run visitor.</param>
        void Accept(ITreeVisitor visitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null);

        bool AccountExists(Address address);

        bool IsDeadAccount(Address address);

        bool IsEmptyAccount(Address address);
        bool HasStateForRoot(Hash256 stateRoot);
    }
}
