// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Blockchain
{
    public class SpecificBlockReadOnlyStateProvider(IStateReader stateReader, BlockHeader? baseBlock) : IReadOnlyStateProvider
    {
        private readonly IStateReader _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));

        public Hash256 StateRoot => BaseBlock?.StateRoot ?? Keccak.EmptyTreeHash;
        public virtual BlockHeader? BaseBlock { get; } = baseBlock;

        public bool TryGetAccount(Address address, out AccountStruct account) => _stateReader.TryGetAccount(BaseBlock, address, out account);

        public bool IsContract(Address address) => TryGetAccount(address, out AccountStruct account) && account.IsContract;

        [SkipLocalsInit]
        public byte[]? GetCode(Address address)
        {
            TryGetAccount(address, out AccountStruct account);
            return !account.HasCode ? [] : _stateReader.GetCode(account.CodeHash);
        }

        public byte[]? GetCode(in ValueHash256 codeHash) => _stateReader.GetCode(in codeHash);

        public void Accept<TCtx>(ITreeVisitor<TCtx> visitor, Hash256 stateRoot, VisitingOptions? visitingOptions) where TCtx : struct, INodeContext<TCtx>
        {
            _stateReader.RunTreeVisitor(visitor, stateRoot, visitingOptions);
        }

        public bool AccountExists(Address address) => _stateReader.TryGetAccount(BaseBlock, address, out _);

        [SkipLocalsInit]
        public bool IsEmptyAccount(Address address) => TryGetAccount(address, out AccountStruct account) && account.IsEmpty;

        public bool HasStateForRoot(BlockHeader? header) => _stateReader.HasStateForRoot(header);

        [SkipLocalsInit]
        public bool IsDeadAccount(Address address) => !TryGetAccount(address, out AccountStruct account) || account.IsEmpty;
    }
}
