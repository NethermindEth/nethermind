// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Rewards
{
    public interface IRewardCalculatorSource
    {
        // TODO: this has been introduced to support AuRa - find a way to remove it from outside of AuRa
        // for example by creating an AuRaRewardCalculator : IRewardCalculator that requires some kind of
        // AuRa ITransactionProcessorFeed
        IRewardCalculator Get(ITransactionProcessor processor);
    }
}
