// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public required string DbSize { get; init; }
}
