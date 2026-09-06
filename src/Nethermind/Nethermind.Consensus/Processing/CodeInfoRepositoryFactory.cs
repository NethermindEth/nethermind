// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Processing;

/// <summary>Builds the <see cref="ICodeInfoRepository"/> a <see cref="BlockAccessListManager"/> tx-processor uses over its world state.</summary>
public delegate ICodeInfoRepository CodeInfoRepositoryFactory(IWorldState worldState);
