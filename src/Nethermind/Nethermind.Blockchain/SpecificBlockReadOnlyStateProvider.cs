// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Blockchain
{
    public class SpecificBlockReadOnlyStateProvider(IStateReader stateReader, BlockHeader? baseBlock) : IReadOnlyStateProvider
    {
        private readonly IStateReader _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));

        public Hash256 StateRoot => BaseBlock?.StateRoot ?? Keccak.EmptyTreeHash;
        public virtual BlockHeader? BaseBlock { get; } = baseBlock;

        public bool TryGetAccount(Address address, out AccountStruct account, int? _ = null) => _stateReader.TryGetAccount(BaseBlock, address, out account);

        public bool IsContract(Address address, int? _ = null) => TryGetAccount(address, out AccountStruct account) && account.IsContract;

        [SkipLocalsInit]
        public byte[]? GetCode(Address address, int? _ = null)
        {
            TryGetAccount(address, out AccountStruct account);
            return !account.HasCode ? [] : _stateReader.GetCode(account.CodeHash);
        }

        public byte[]? GetCode(in ValueHash256 codeHash) => _stateReader.GetCode(in codeHash);

        public bool AccountExists(Address address, int? _ = null) => _stateReader.TryGetAccount(BaseBlock, address, out AccountStruct account);

        [SkipLocalsInit]
        public bool IsDeadAccount(Address address, int? _ = null) => !TryGetAccount(address, out AccountStruct account) || account.IsEmpty;
    }
}
