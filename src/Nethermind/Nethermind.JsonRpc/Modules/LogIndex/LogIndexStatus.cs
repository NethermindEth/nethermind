// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc.Modules.LogIndex;

// TODO: add forward/backward sync status?
public class LogIndexStatus
{
    public class Range
    {
        public required int? FromBlock { get; init; }
        public required int? ToBlock { get; init; }
    }

    public required Range Current { get; init; }
    public required Range Target { get; init; }
    public bool IsRunning { get; init; }
    public DateTime? LastUpdateUtc { get; init; }
    public string? LastError { get; init; }
    public required string DbSize { get; init; }
}
