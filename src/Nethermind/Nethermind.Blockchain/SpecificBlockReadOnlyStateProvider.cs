// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Blockchain
{
    public class SpecificBlockReadOnlyStateProvider : IReadOnlyStateProvider
    {
        private readonly IStateReader _stateReader;

        public SpecificBlockReadOnlyStateProvider(IStateReader stateReader, Hash256? stateRoot = null)
        {
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            StateRoot = stateRoot ?? Keccak.EmptyTreeHash;
        }

        public virtual Hash256 StateRoot { get; }

        public bool TryGetAccount(Address address, out AccountStruct account) => _stateReader.TryGetAccount(StateRoot, address, out account);

        public bool HasStateForRoot(Hash256 stateRoot) => _stateReader.HasStateForRoot(stateRoot);
        public void Accept(ITreeVisitor visitor, Hash256 stateRoot, VisitingOptions? visitingOptions) => _stateReader.RunTreeVisitor(visitor, stateRoot, visitingOptions);

        public byte[]? GetCode(Address address) => TryGetAccount(address, out AccountStruct account) && account.HasCode ? _stateReader.GetCode(account.CodeHash) : Array.Empty<byte>();
        public byte[]? GetCode(Hash256 codeHash) => _stateReader.GetCode(codeHash);
        public byte[]? GetCode(ValueHash256 codeHash) => _stateReader.GetCode(codeHash);
    }
}
