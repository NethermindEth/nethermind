// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Transactions;

namespace Nethermind.Consensus.Producers;

public interface IBlockProducerTxSourceFactory
{
    ITxSource Create();
}
