// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain;

public class VerificationProgressArgs : EventArgs
{
    public VerificationProgressArgs(long processed, long totalToProcess, TimeSpan elapsed)
    {
        Processed = processed;
        TotalToProcess = totalToProcess;
        Elapsed = elapsed;
    }

    public long Processed { get; }
    public long TotalToProcess { get; }
    public TimeSpan Elapsed { get; }
}
