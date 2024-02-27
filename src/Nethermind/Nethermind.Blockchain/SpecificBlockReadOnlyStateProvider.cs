// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Blockchain
{
    public class SpecificBlockReadOnlyStateProvider(IStateReader stateReader, Hash256? stateRoot = null) : IReadOnlyStateProvider
    {
        private readonly IStateReader _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));

        public virtual Hash256 StateRoot { get; } = stateRoot ?? Keccak.EmptyTreeHash;

        public bool TryGetAccount(Address address, out AccountStruct account) => _stateReader.TryGetAccount(StateRoot, address, out account);

        public bool IsContract(Address address) => TryGetAccount(address, out AccountStruct account) && account.IsContract;

        [SkipLocalsInit]
        public byte[]? GetCode(Address address)
        {
            TryGetAccount(address, out AccountStruct account);
            return !account.HasCode ? Array.Empty<byte>() : _stateReader.GetCode(account.CodeHash);
        }

        public byte[]? GetCode(Hash256 codeHash) => _stateReader.GetCode(codeHash);
        public byte[]? GetCode(ValueHash256 codeHash) => _stateReader.GetCode(codeHash);

        public void Accept(ITreeVisitor visitor, Hash256 stateRoot, VisitingOptions? visitingOptions)
        {
            _stateReader.RunTreeVisitor(visitor, stateRoot, visitingOptions);
        }

        public bool AccountExists(Address address) => _stateReader.TryGetAccount(StateRoot, address, out _);

        [SkipLocalsInit]
        public bool IsEmptyAccount(Address address) => TryGetAccount(address, out AccountStruct account) && account.IsEmpty;
        public bool HasStateForRoot(Hash256 stateRoot) => _stateReader.HasStateForRoot(stateRoot);

        [SkipLocalsInit]
        public bool IsDeadAccount(Address address) => !TryGetAccount(address, out AccountStruct account) || account.IsEmpty;
    }
}
