// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.RpcTests.Monitor;

internal interface IMonitorStats
{
    void RecordTestRun();
    void RecordRequestRun();
    void RecordTestFailure();
    void RecordError();
}

internal sealed class NullMonitorStats : IMonitorStats
{
    public static readonly NullMonitorStats Instance = new();

    public void RecordTestRun() { }
    public void RecordRequestRun() { }
    public void RecordTestFailure() { }
    public void RecordError() { }
}
