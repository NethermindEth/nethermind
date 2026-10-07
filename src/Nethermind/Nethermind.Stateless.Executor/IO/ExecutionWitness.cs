// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Consensus.Stateless;
using Nethermind.Core.Collections;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial struct ExecutionWitness
{
    [SszProgressiveList]
    public SszWitnessState[] State { get; set; }

    [SszProgressiveList]
    public SszWitnessCodes[] Codes { get; set; }

    [SszList(0x100)]
    public SszWitnessHeader[] Headers { get; set; }

    public static ExecutionWitness From(Witness witness)
    {
        SszWitnessCodes[] codes = new SszWitnessCodes[witness.Codes.Count];

        for (int i = 0; i < codes.Length; i++)
            codes[i] = new() { Bytes = new WitnessCode(witness.Codes[i].AsMemory()) };

        SszWitnessHeader[] headers = new SszWitnessHeader[witness.Headers.Count];

        for (int i = 0; i < headers.Length; i++)
            headers[i] = new() { Bytes = witness.Headers[i] };

        SszWitnessState[] state = new SszWitnessState[witness.State.Count];

        for (int i = 0; i < state.Length; i++)
            state[i] = new() { Bytes = witness.State[i] };

        return new()
        {
            Codes = codes,
            Headers = headers,
            State = state
        };
    }

    public readonly Witness ToWitness()
    {
        ArrayPoolList<byte[]> headers = new(Headers.Length, Headers.Length);
        Span<byte[]> headersSpan = headers.AsSpan();

        for (int i = 0; i < Headers.Length; i++)
            headersSpan[i] = Headers[i].Bytes;

        return new()
        {
            Codes = new CodeList(Codes),
            Headers = headers,
            Keys = ArrayPoolList<byte[]>.Empty(),
            State = new StateNodeList(State)
        };
    }

    /// <summary>The decoded state nodes as the list of their bytes, read in place rather than copied into a list of their own.</summary>
    /// <remarks>The witness of a block holds tens of thousands of state nodes, and the zkVM guest pays for every one it copies.</remarks>
    private sealed class StateNodeList(SszWitnessState[] nodes) : IOwnedReadOnlyList<byte[]>
    {
        public int Count => nodes.Length;

        public byte[] this[int index] => nodes[index].Bytes;

        /// <inheritdoc/>
        /// <remarks><see cref="SszWitnessState"/> wraps a lone array, so an array of them is laid out as one of array references.</remarks>
        public ReadOnlySpan<byte[]> AsSpan()
        {
            Debug.Assert(Unsafe.SizeOf<SszWitnessState>() == Unsafe.SizeOf<byte[]>());
            return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<SszWitnessState, byte[]>(ref MemoryMarshal.GetArrayDataReference(nodes)), nodes.Length);
        }

        public IEnumerator<byte[]> GetEnumerator()
        {
            foreach (SszWitnessState node in nodes)
                yield return node.Bytes;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose() { }
    }

    /// <summary>The decoded codes, read in place as the memory they were decoded into, or as arrays copied on request.</summary>
    /// <remarks>The witness code store reads the memory, so the zkVM guest runs each code without copying it again.</remarks>
    private sealed class CodeList(SszWitnessCodes[] codes) : IOwnedReadOnlyList<byte[]>, IReadOnlyList<ReadOnlyMemory<byte>>
    {
        public int Count => codes.Length;

        public byte[] this[int index] => codes[index].Bytes.Code.ToArray();

        ReadOnlyMemory<byte> IReadOnlyList<ReadOnlyMemory<byte>>.this[int index] => codes[index].Bytes.Code;

        public ReadOnlySpan<byte[]> AsSpan()
        {
            byte[][] arrays = new byte[codes.Length][];
            for (int i = 0; i < arrays.Length; i++)
                arrays[i] = this[i];

            return arrays;
        }

        public IEnumerator<byte[]> GetEnumerator()
        {
            foreach (SszWitnessCodes code in codes)
                yield return code.Bytes.Code.ToArray();
        }

        IEnumerator<ReadOnlyMemory<byte>> IEnumerable<ReadOnlyMemory<byte>>.GetEnumerator()
        {
            foreach (SszWitnessCodes code in codes)
                yield return code.Bytes.Code;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose() { }
    }
}

[SszContainer(isCollectionItself: true)]
public partial struct SszWitnessCodes
{
    [SszList(0x1_0000)]
    public WitnessCode Bytes { get; set; }
}

/// <summary>A witness code.</summary>
public sealed class WitnessCode
{
    /// <summary>Copies <paramref name="code"/> into executable code memory, which the EVM runs it from without another copy.</summary>
    public WitnessCode(ReadOnlySpan<byte> code) => Code = ExecutableCodeMemory.Copy(code);

    /// <summary>Wraps <paramref name="code"/> as it is.</summary>
    internal WitnessCode(ReadOnlyMemory<byte> code) => Code = code;

    public ReadOnlyMemory<byte> Code { get; }

    public ReadOnlySpan<byte> AsSpan() => Code.Span;
}

[SszContainer(isCollectionItself: true)]
public partial struct SszWitnessHeader
{
    [SszList(0x400)]
    public byte[] Bytes { get; set; }
}

/// <remarks>Must keep <see cref="Bytes"/> as its only field: the witness reads an array of these as one of byte arrays.</remarks>
[SszContainer(isCollectionItself: true)]
public partial struct SszWitnessState
{
    [SszList(0x400)]
    public byte[] Bytes { get; set; }
}
