// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.OpcodeTracing.Plugin.Output;

namespace Nethermind.OpcodeTracing.Plugin.Tracing;

/// <summary>
/// Thread-safe accumulator for opcode occurrence counts.
/// </summary>
public sealed class OpcodeCounter
{
    private const int OpcodeSpace = 256;
    private readonly long[] _counters = new long[OpcodeSpace];
    private readonly object _lock = new();

    /// <summary>
    /// Gets the total count of all opcodes.
    /// </summary>
    public long TotalOpcodes => _counters.Sum();

    /// <summary>
    /// Increments the count for the specified opcode.
    /// </summary>
    /// <param name="opcode">The opcode byte value.</param>
    public void Increment(byte opcode)
    {
        Interlocked.Increment(ref _counters[opcode]);
    }

    /// <summary>
    /// Accumulates counts from a source array into this counter.
    /// </summary>
    /// <param name="source">The source array of counts to add.</param>
    public void AccumulateFrom(long[] source)
    {
        if (source is null || source.Length != OpcodeSpace)
        {
            throw new ArgumentException("Source array must have 256 elements", nameof(source));
        }

        lock (_lock)
        {
            for (int i = 0; i < OpcodeSpace; i++)
            {
                if (source[i] > 0)
                {
                    _counters[i] += source[i];
                }
            }
        }
    }

    /// <summary>
    /// Gets an immutable snapshot of current opcode counts.
    /// </summary>
    /// <returns>A copy of the current counter array.</returns>
    public long[] GetSnapshot()
    {
        lock (_lock)
        {
            return (long[])_counters.Clone();
        }
    }

    /// <summary>
    /// Converts the opcode counts to a dictionary with human-readable labels.
    /// </summary>
    /// <returns>A dictionary mapping opcode names to counts.</returns>
    public Dictionary<string, long> ToOpcodeCountsDictionary()
    {
        var snapshot = GetSnapshot();
        var result = new Dictionary<string, long>();

        for (int i = 0; i < OpcodeSpace; i++)
        {
            if (snapshot[i] > 0)
            {
                string label = OpcodeLabelCache.GetLabel((byte)i);
                result[label] = snapshot[i];
            }
        }

        return result;
    }

}
