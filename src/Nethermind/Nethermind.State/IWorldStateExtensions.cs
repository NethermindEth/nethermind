// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State
{
    public static class WorldStateExtensions
    {
        public static void AddBalance(this IWorldState stateProvider, Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec, bool isSystemTx)
        {
            if (!isSystemTx && !stateProvider.AccountExists(address))
            {
                throw new InvalidOperationException("Updating balance of a non-existing account");
            }
            stateProvider.AddToBalance(address, balanceChange, releaseSpec);
        }

        public static void SubtractBalance(this IWorldState stateProvider, Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec, bool isSystemTx)
        {
            if (!isSystemTx && !stateProvider.AccountExists(address))
            {
                throw new InvalidOperationException("Updating balance of a non-existing account");
            }
            stateProvider.SubtractFromBalance(address, balanceChange, releaseSpec);
        }

        public static byte[] GetCode(this IWorldState stateProvider, Address address)
        {
            stateProvider.TryGetAccount(address, out AccountStruct account);
            return !account.HasCode ? Array.Empty<byte>() : stateProvider.GetCode(account.CodeHash) ?? Array.Empty<byte>();
        }

        public static void InsertCode(this IWorldState worldState, Address address, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isSystemCall = false, bool isGenesis = false)
        {
            if (isSystemCall && address == Address.SystemUser) return;
            Hash256 codeHash = code.Length == 0 ? Keccak.OfAnEmptyString : Keccak.Compute(code.Span);
            worldState.InsertCode(address, codeHash, code, spec, isGenesis);
        }

        public static string DumpState(this IWorldState stateProvider)
        {
            TreeDumper dumper = new();
            stateProvider.Accept(dumper, stateProvider.StateRoot);
            return dumper.ToString();
        }
    }
}
