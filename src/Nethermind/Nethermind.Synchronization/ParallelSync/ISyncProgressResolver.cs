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
        long FindBestFullState();

        long FindBestHeader();

        long FindBestFullBlock();

        bool IsFastBlocksHeadersFinished();

        bool IsFastBlocksBodiesFinished();

        bool IsFastBlocksReceiptsFinished();

        bool IsLoadingBlocksFromDb();

        long FindBestProcessedBlock();

        bool IsGetRangesFinished();


        UInt256 ChainDifficulty { get; }

        UInt256? GetTotalDifficulty(Keccak blockHash);

        void RecalculateProgressPointers();
    }
}
