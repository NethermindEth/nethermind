// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Processing;

public interface IBlockProcessingPauseControl
{
    void Pause();

    void Resume();

    bool IsPaused { get; }
}
