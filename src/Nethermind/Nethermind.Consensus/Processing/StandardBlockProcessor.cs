// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

/// <summary>Sealed default processor for standard block processing; specialized chains retain the extensible base.</summary>
public sealed class StandardBlockProcessor(
    ISpecProvider specProvider, IBlockValidator blockValidator, IRewardCalculator rewardCalculator,
    IBlockProcessor.IBlockTransactionsExecutor executor, IWorldState state, IReceiptStorage receipts,
    IBeaconBlockRootHandler beaconRoot, IBlockhashStore blockHashes, ILogManager logManager,
    IWithdrawalProcessor withdrawals, IExecutionRequestsProcessor requests, IBlockAccessListManager balManager)
    : BlockProcessor(specProvider, blockValidator, rewardCalculator, executor, state, receipts,
        beaconRoot, blockHashes, logManager, withdrawals, requests, balManager);
