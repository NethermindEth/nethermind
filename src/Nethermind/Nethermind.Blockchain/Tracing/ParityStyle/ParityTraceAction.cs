// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

public class ParityTraceAction
{
    public CappedArray<int> TraceAddress { get; set; }
    public string? CallType { get; set; }
    public bool IncludeInTrace { get; set; } = true;
    public bool IsPrecompiled { get; set; }
    public string? Type { get; set; }
    public string? CreationMethod { get; set; }
    public Address? From { get; set; }
    public Address? To { get; set; }
    public long Gas { get; set; }
    public UInt256 Value { get; set; }
    public byte[]? Input { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ParityTraceResult? Result { get; set; } = new();

    public List<ParityTraceAction> Subtraces { get; set; } = [];

    /// <summary>
    /// Cached count of <see cref="Subtraces"/> entries with <see cref="IncludeInTrace"/>
    /// equal to <see langword="true"/>. Maintained incrementally by the tracer's
    /// <c>PushAction</c> path so JSON converters and per-call <c>traceAddress</c>
    /// indexing don't need to do a LINQ <c>Count</c> over the list, and so the
    /// streaming tracer can read it without holding the list at all.
    /// </summary>
    [JsonIgnore]
    public int IncludedSubtraceCount { get; set; }

    public Address? Author { get; set; }
    public string? RewardType { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// Restores this instance to the state of a freshly-constructed action so it can be
    /// reused from a pool. Keeps the existing <see cref="Result"/> (resetting its fields)
    /// and <see cref="Subtraces"/> list backing array intact — callers should have already
    /// returned any child actions in <see cref="Subtraces"/> to their pool before resetting.
    /// </summary>
    public void Reset()
    {
        TraceAddress = default;
        CallType = null;
        IncludeInTrace = true;
        IsPrecompiled = false;
        Type = null;
        CreationMethod = null;
        From = null;
        To = null;
        Gas = 0;
        Value = default;
        Input = null;
        if (Result is null)
        {
            Result = new ParityTraceResult();
        }
        else
        {
            Result.GasUsed = 0;
            Result.Output = null;
            Result.Address = null;
            Result.Code = null;
        }
        Subtraces.Clear();
        IncludedSubtraceCount = 0;
        Author = null;
        RewardType = null;
        Error = null;
    }
}
