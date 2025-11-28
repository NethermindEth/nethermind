// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public class AbortParallelExecutionException(in Version blockingRead) : Exception
{
    public Version BlockingRead { get; } = blockingRead;
}
