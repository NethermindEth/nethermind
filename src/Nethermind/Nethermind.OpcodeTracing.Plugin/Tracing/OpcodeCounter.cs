// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;

namespace Nethermind.OpcodeTracing.Plugin.Tracing;

/// <summary>
/// Thread-safe accumulator for opcode occurrence counts using ConcurrentDictionary.
/// </summary>
public sealed class OpcodeCounter
{
    private readonly ConcurrentDictionary<byte, long> _counters = new();

    /// <summary>
    /// Gets the total count of all opcodes.
    /// </summary>
    public long TotalOpcodes
    {
        get
        {
            long total = 0;
            foreach (long count in _counters.Values)
            {
                total += count;
            }
            return total;
        }
    }

    /// <summary>
    /// Increments the count for the specified opcode.
    /// </summary>
    /// <param name="opcode">The opcode byte value.</param>
    public void Increment(byte opcode)
    {
        _counters.AddOrUpdate(opcode, 1, static (_, oldValue) => oldValue + 1);
    }

    /// <summary>
    /// Accumulates counts from a source dictionary into this counter.
    /// Used by RealTime mode where block trace is already a dictionary.
    /// </summary>
    /// <param name="source">The source dictionary of opcode counts to add.</param>
    public void AccumulateFrom(IReadOnlyDictionary<byte, long> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        foreach ((byte opcode, long count) in source)
        {
            if (count > 0)
            {
                _counters.AddOrUpdate(opcode, count, (_, oldValue) => oldValue + count);
            }
        }
    }

    /// <summary>
    /// Accumulates counts from a source array into this counter.
    /// Used by RetrospectiveExecution mode where block opcodes are accumulated in an array first.
    /// </summary>
    /// <param name="source">The source array of counts to add (must have 256 elements).</param>
    public void AccumulateFrom(long[] source)
    {
        if (source is null || source.Length != 256)
        {
            throw new ArgumentException("Source array must have 256 elements", nameof(source));
        }

        for (int i = 0; i < 256; i++)
        {
            long count = source[i];
            if (count > 0)
            {
                byte opcode = (byte)i;
                _counters.AddOrUpdate(opcode, count, (_, oldValue) => oldValue + count);
            }
        }
    }

    /// <summary>
    /// Gets an immutable snapshot of current opcode counts as a dictionary.
    /// </summary>
    /// <returns>A dictionary mapping opcode bytes to counts.</returns>
    public Dictionary<byte, long> ToOpcodeCountsDictionary()
    {
        return new Dictionary<byte, long>(_counters);
    }
}
