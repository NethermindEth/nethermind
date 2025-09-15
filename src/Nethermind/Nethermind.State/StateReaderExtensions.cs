// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State
{
    public static class StateReaderExtensions
    {
        public static UInt256 GetNonce(this IStateReader stateReader, BlockHeader? baseBlock, Address address)
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

        public static byte[] GetCode(this IStateReader stateReader, BlockHeader? baseBlock, Address address)
        {
            return stateReader.GetCode(GetCodeHash(stateReader, baseBlock, address)) ?? [];
        }

        public static ValueHash256 GetCodeHash(this IStateReader stateReader, BlockHeader? baseBlock, Address address)
        {
            stateReader.TryGetAccount(baseBlock, address, out AccountStruct account);
            return account.CodeHash;
        }

        public static TrieStats CollectStats(this IStateReader stateProvider, Hash256 root, IKeyValueStore codeStorage, ILogManager logManager, ProgressLogger progressLogger, CancellationToken cancellationToken = default)
        {
            TrieStatsCollector collector = new(codeStorage, logManager, progressLogger, cancellationToken);
            stateProvider.RunTreeVisitor(collector, root, new VisitingOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                FullScanMemoryBudget = 16.GiB(), // Gonna guess that if you are running this, you have a decent setup.
            });
            return collector.Stats;
        }

        public static string DumpState(this IStateReader stateReader, Hash256 root)
        {
            TreeDumper dumper = new();
            stateReader.RunTreeVisitor(dumper, root);
            return dumper.ToString();
        }
    }
}
