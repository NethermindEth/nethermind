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

/// <summary>Contract code from the witness, held in executable code memory when decoded so the EVM runs it in place.</summary>
public sealed partial class WitnessCode
{
    /// <summary>Holds <paramref name="code"/>, decoded from the input, in executable code memory, which the EVM runs it
    /// from without another copy.</summary>
    /// <remarks>See <c>WitnessCode.std.cs</c> and <c>.zkevm.cs</c>.</remarks>
    public WitnessCode(ReadOnlySpan<byte> code) => Code = Hold(code);

    /// <summary>Wraps <paramref name="code"/> as it is.</summary>
    internal WitnessCode(ReadOnlyMemory<byte> code) => Code = code;

    public ReadOnlyMemory<byte> Code { get; private set; }

    public ReadOnlySpan<byte> AsSpan() => Code.Span;

    private static partial ReadOnlyMemory<byte> Hold(ReadOnlySpan<byte> code);

    /// <summary>Moves the decoded <paramref name="codes"/> that still lie in the input to the front of
    /// <paramref name="consumed"/>, the decoded input, which nothing else reads again.</summary>
    /// <returns>The number of bytes at the front of <paramref name="consumed"/> the codes now take.</returns>
    /// <remarks>See <c>WitnessCode.std.cs</c> and <c>.zkevm.cs</c>.</remarks>
    internal static partial int PlaceInConsumedInput(SszWitnessCodes[] codes, ReadOnlySpan<byte> consumed);

    /// <summary>Moves <paramref name="codes"/>, which lie in <paramref name="consumed"/>, to its front, each padded for
    /// execution.</summary>
    /// <returns>The number of bytes at the front of <paramref name="consumed"/> the codes now take.</returns>
    /// <remarks>A code goes where it overwrites no code still to be moved; one that would is copied to fresh memory
    /// instead, as are all of them unless they lie in order.</remarks>
    internal static unsafe int MoveToFront(SszWitnessCodes[] codes, ReadOnlySpan<byte> consumed)
    {
        byte* start = Address(consumed);
        byte* end = start + consumed.Length;

        // In order, no code lies before the next one to move; inside the input, none is written past its end.
        byte* previousEnd = start;
        for (int i = 0; i < codes.Length; i++)
        {
            ReadOnlySpan<byte> code = codes[i].Bytes.Code.Span;
            if (Address(code) < previousEnd)
                return CopyAll(codes);

            previousEnd = Address(code) + code.Length;
        }

        if (previousEnd > end)
            return CopyAll(codes);

        byte* free = Align(start);
        for (int i = 0; i < codes.Length; i++)
        {
            WitnessCode code = codes[i].Bytes;
            ReadOnlySpan<byte> source = code.Code.Span;
            byte* next = i + 1 < codes.Length ? Address(codes[i + 1].Bytes.Code.Span) : end;
            byte* placedEnd = free + source.Length + CodeInfo.ExecutionPadding;

            if (placedEnd <= next)
            {
                code.Code = ExecutableCodeMemory.Place(new Span<byte>(free, (int)(placedEnd - free)), source);
                free = Align(placedEnd);
            }
            else
            {
                code.Code = ExecutableCodeMemory.Copy(source);
            }
        }

        return free > end ? consumed.Length : (int)(free - start);
    }

    private static int CopyAll(SszWitnessCodes[] codes)
    {
        foreach (SszWitnessCodes code in codes)
            code.Bytes.Code = ExecutableCodeMemory.Copy(code.Bytes.Code.Span);

        return 0;
    }

    private static unsafe byte* Address(ReadOnlySpan<byte> span) => (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));

    private static unsafe byte* Align(byte* pointer) => (byte*)(((nuint)pointer + 7) & ~(nuint)7);
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
