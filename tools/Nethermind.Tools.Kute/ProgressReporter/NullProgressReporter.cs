// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.ProgressReporter;

public class NullProgressReporter : IProgressReporter
{

    public void ReportProgress(int n)
    {
    }
    public void ReportComplete()
    {
    }
}
