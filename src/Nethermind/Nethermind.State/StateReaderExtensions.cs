// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State
{
    public static class StateReaderExtensions
    {
        public static UInt256 GetNonce(this IStateReader stateReader, Hash256 stateRoot, Address address) =>
            stateReader.TryGetAccount(stateRoot, address, out AccountStruct account) ? account.Nonce : UInt256.Zero;

        public static UInt256 GetBalance(this IStateReader stateReader, Hash256 stateRoot, Address address) =>
            stateReader.TryGetAccount(stateRoot, address, out AccountStruct account) ? account.Balance : UInt256.Zero;

        public static ValueHash256 GetStorageRoot(this IStateReader stateReader, Hash256 stateRoot, Address address) =>
            stateReader.TryGetAccount(stateRoot, address, out AccountStruct account) ? account.StorageRoot : Keccak.EmptyTreeHash.ValueHash256;

        public static byte[] GetCode(this IStateReader stateReader, Hash256 stateRoot, Address address) =>
            stateReader.GetCode(GetCodeHash(stateReader, stateRoot, address)) ?? Array.Empty<byte>();

        public static ValueHash256 GetCodeHash(this IStateReader stateReader, Hash256 stateRoot, Address address) =>
            stateReader.TryGetAccount(stateRoot, address, out AccountStruct account) ? account.CodeHash : Keccak.OfAnEmptyString.ValueHash256;

        public static bool HasStateForBlock(this IStateReader stateReader, BlockHeader header) =>
            stateReader.HasStateForRoot(header.StateRoot!);
    }
}
