// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
namespace Nethermind.Synchronization.ParallelSync
{
    public interface ISyncProgressResolver
    {
        void UpdateBarriers();

        long FindBestFullState();

        long FindBestHeader();

        long FindBestFullBlock();

        bool IsFastBlocksHeadersFinished();

        bool IsFastBlocksBodiesFinished();

        bool IsFastBlocksReceiptsFinished();

        bool IsLoadingBlocksFromDb();

        long FindBestProcessedBlock();

        bool IsSnapGetRangesFinished();


        UInt256 ChainDifficulty { get; }

        UInt256? GetTotalDifficulty(Hash256 blockHash);

        void RecalculateProgressPointers();
    }
}
