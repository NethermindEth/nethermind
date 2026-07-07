// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Processing;

/// <summary>
/// A <see cref="ITxProcessorWithWorldStateManager"/> that reuses a single worker for the whole block.
/// </summary>
internal interface ISequentialTxProcessorWithWorldStateManager : ITxProcessorWithWorldStateManager;
