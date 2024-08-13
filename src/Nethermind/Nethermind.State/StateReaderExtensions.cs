// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State
{
    public static class StateReaderExtensions
    {
        public static UInt256 GetNonce(this IStateReader stateReader, Hash256 stateRoot, Address address)
        {
            stateReader.TryGetAccount(stateRoot, address, out AccountStruct account);
            return account.Nonce;
        }

        public static UInt256 GetBalance(this IStateReader stateReader, Hash256 stateRoot, Address address)
        {
            stateReader.TryGetAccount(stateRoot, address, out AccountStruct account);
            return account.Balance;
        }

        public static ValueHash256 GetStorageRoot(this IStateReader stateReader, Hash256 stateRoot, Address address)
        {
            stateReader.TryGetAccount(stateRoot, address, out AccountStruct account);
            return account.StorageRoot;
        }

        public static byte[] GetCode(this IStateReader stateReader, Hash256 stateRoot, Address address)
        {
            return stateReader.GetCode(GetCodeHash(stateReader, stateRoot, address)) ?? Array.Empty<byte>();
        }

        public static ValueHash256 GetCodeHash(this IStateReader stateReader, Hash256 stateRoot, Address address)
        {
            stateReader.TryGetAccount(stateRoot, address, out AccountStruct account);
            return account.CodeHash;
        }

        public static bool HasStateForBlock(this IStateReader stateReader, BlockHeader header)
        {
            return stateReader.HasStateForRoot(header.StateRoot!);
        }

        public static TrieStats CollectStats(this IStateReader stateProvider, Hash256 root, IKeyValueStore codeStorage, ILogManager logManager)
        {
            TrieStatsCollector collector = new(codeStorage, logManager);
            stateProvider.RunTreeVisitor(collector, root, new VisitingOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                FullScanMemoryBudget = 16.GiB(), // Gonna guess that if you are running this, you have a decent setup.
            });
            return collector.Stats;
        }
    }
}
