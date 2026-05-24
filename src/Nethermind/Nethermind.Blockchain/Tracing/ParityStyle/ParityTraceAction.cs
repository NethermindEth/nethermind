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
    /// Count of <see cref="Subtraces"/> with <see cref="IncludeInTrace"/> = <see langword="true"/>,
    /// maintained incrementally by <c>PushAction</c>. Lets streaming tracers carry the
    /// count without holding the list.
    /// </summary>
    [JsonIgnore]
    public int IncludedSubtraceCount { get; set; }

    public Address? Author { get; set; }
    public string? RewardType { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// Restores this instance to its just-constructed state for pool reuse. Keeps the
    /// existing <see cref="Result"/> object and <see cref="Subtraces"/> backing array.
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
