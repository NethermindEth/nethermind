// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
        public static UInt256 GetNonce(this IStateReader stateReader, Keccak stateRoot, Address address)
        {
            return stateReader.GetAccount(stateRoot, address)?.Nonce ?? UInt256.Zero;
        }

        public static UInt256 GetBalance(this IStateReader stateReader, Keccak stateRoot, Address address)
        {
            return stateReader.GetAccount(stateRoot, address)?.Balance ?? UInt256.Zero;
        }

        public static Keccak GetStorageRoot(this IStateReader stateReader, Keccak stateRoot, Address address)
        {
            return stateReader.GetAccount(stateRoot, address)?.StorageRoot ?? Keccak.EmptyTreeHash;
        }

        public static byte[] GetCode(this IStateReader stateReader, Keccak stateRoot, Address address)
        {
            return stateReader.GetCode(GetCodeHash(stateReader, stateRoot, address)) ?? Array.Empty<byte>();
        }

        public static Keccak GetCodeHash(this IStateReader stateReader, Keccak stateRoot, Address address)
        {
            return stateReader.GetAccount(stateRoot, address)?.CodeHash ?? Keccak.OfAnEmptyString;
        }

        public static bool HasStateForBlock(this IStateReader stateReader, BlockHeader header)
        {
            RootCheckVisitor rootCheckVisitor = new();
            stateReader.RunTreeVisitor(rootCheckVisitor, header.StateRoot);
            return rootCheckVisitor.HasRoot;
        }
    }
}
