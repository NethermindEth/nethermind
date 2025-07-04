// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;

namespace Nethermind.Network.Optimum.Fuzzer;

public sealed class ClientReport
{
    public int Id { get; set; }
    public int Messages { get; set; }
}

public class ClientException(string reason) : Exception(reason);

internal sealed class RunReport
{
    public ReportStatus Status { get; set; } = new ReportStatus.Completed();
    public ClientReport[] Subscribers { get; set; }
    public ClientReport[] Publishers { get; set; }

    public RunReport(int subscriberCount, int publisherCount)
    {
        Subscribers = Enumerable.Range(0, subscriberCount)
            .Select(id => new ClientReport { Id = id })
            .ToArray();
        Publishers = Enumerable.Range(0, publisherCount)
            .Select(id => new ClientReport { Id = id })
            .ToArray();
    }
}

internal abstract class ReportStatus
{
    public class Completed : ReportStatus
    {
        override public string ToString() => "Completed";
    }

    public class TimedOut(TimeSpan timeout) : ReportStatus
    {
        public override string ToString() => $"Timed out after {timeout.TotalMilliseconds} ms";
    }

    public class Failed(Exception exception) : ReportStatus
    {
        public override string ToString() => $"Failed due to '{exception.Message}'";
    };
}
