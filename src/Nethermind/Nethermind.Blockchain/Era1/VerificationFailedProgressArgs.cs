// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain;

public class VerificationFailedProgressArgs
{
    public VerificationFailedProgressArgs(string fileName)
    {
        FailedArchiveName = fileName;
    }
    public string FailedArchiveName { get; }
}
