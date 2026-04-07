// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc.Modules.LogIndex;

public class LogIndexStatus
{
    public readonly record struct Range(int? FromBlock, int? ToBlock);

    public required Range Current { get; init; }
    public required Range Target { get; init; }
    public bool IsRunning { get; init; }
    public DateTimeOffset? LastUpdate { get; init; }
    public string? LastError { get; init; }
    public required string DbSize { get; init; }
}
