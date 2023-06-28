// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public interface IReadOnlyTxProcessingEnvBase
{
    IStateReader StateReader { get; }
    IWorldState StateProvider { get; }
    IBlockTree BlockTree { get; }
}
