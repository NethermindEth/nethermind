// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State
{
    public static class StateReaderExtensions
    {
        public static ulong GetNonce(this IStateReader stateReader, BlockHeader? baseBlock, Address address)
        {
            stateReader.TryGetAccount(baseBlock, address, out AccountStruct account);
            return account.Nonce;
        }

        public static UInt256 GetBalance(this IStateReader stateReader, BlockHeader? baseBlock, Address address)
        {
            stateReader.TryGetAccount(baseBlock, address, out AccountStruct account);
            return account.Balance;
        }

        public static ValueHash256 GetStorageRoot(this IStateReader stateReader, BlockHeader? baseBlock, Address address)
        {
            stateReader.TryGetAccount(baseBlock, address, out AccountStruct account);
            return account.StorageRoot;
        }

        public static byte[] GetCode(this IStateReader stateReader, BlockHeader? baseBlock, Address address) => stateReader.GetCode(GetCodeHash(stateReader, baseBlock, address)) ?? [];

        public static ValueHash256 GetCodeHash(this IStateReader stateReader, BlockHeader? baseBlock, Address address)
        {
            stateReader.TryGetAccount(baseBlock, address, out AccountStruct account);
            return account.CodeHash;
        }

        public static string DumpState(this IStateReader stateReader, BlockHeader? baseBlock)
        {
            TreeDumper dumper = new();
            stateReader.RunTreeVisitor(dumper, baseBlock);
            return dumper.ToString();
        }
    }
}
