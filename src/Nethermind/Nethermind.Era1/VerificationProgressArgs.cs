// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1;

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
