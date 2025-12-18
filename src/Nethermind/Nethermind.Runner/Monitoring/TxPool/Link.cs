// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Runner.Monitoring.TransactionPool;

internal class Link(string source, string target, long value)
{
    public string Source { get; } = source;
    public string Target { get; } = target;
    public long Value { get; } = value;
}
