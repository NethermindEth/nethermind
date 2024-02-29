// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.CodeAnalysis
{

    public interface ICode
    {
        byte this[int index] { get; }
        int Length { get; }

        ZeroPaddedSpan SliceWithZeroPadding(scoped in UInt256 startIndex, int length,
            PadDirection padDirection = PadDirection.Right);

        byte[] ToBytes();
        Span<byte> Slice(int index, int length);
    }

    public readonly struct ByteCode: ICode
    {
        public ByteCode(byte[] code)
        {
            MachineCode = code;
        }

        public byte[] MachineCode { get; }

        public byte this[int index]
        {
            get => MachineCode[index];
        }

        public int Length => MachineCode.Length;

        public ZeroPaddedSpan SliceWithZeroPadding(scoped in UInt256 startIndex, int length, PadDirection padDirection = PadDirection.Right) => MachineCode.SliceWithZeroPadding(startIndex, length, padDirection);

        public byte[] ToBytes() => MachineCode.ToArray();

        public Span<byte> Slice(int index, int length) => MachineCode.Slice(index, length);
    }

    public readonly struct VerkleCode: ICode
    {
        private IWorldState WorldState { get; }
        private Address Owner { get; }
        public VerkleCode(IWorldState worldState, Address codeOwner)
        {
            if (worldState.StateType != StateType.Verkle) throw new NotSupportedException("verkle state needed");
            Owner = codeOwner;
            WorldState = worldState;
            Length = (int)worldState.GetAccount(codeOwner).CodeSize;
        }

        public byte this[int index]
        {
            get
            {
                int chunkId = index / 31;
                int chunkLoc = index % 31;
                return WorldState.GetCodeChunk(Owner, (UInt256)chunkId)[chunkLoc];
            }
        }

        public int Length { get; init; }

        public ZeroPaddedSpan SliceWithZeroPadding(scoped in UInt256 startIndex, int length,
            PadDirection padDirection = PadDirection.Right)
        {
            if (startIndex >= Length || startIndex > int.MaxValue)
            {
                return new ZeroPaddedSpan(default, length, PadDirection.Right);
            }

            Span<byte> toReturn = Slice((int)startIndex, length);
            return new ZeroPaddedSpan(toReturn, length - toReturn.Length, padDirection);

        }

        public byte[] ToBytes() => Slice(0, Length).ToArray();

        public Span<byte> Slice(int index, int length)
        {
            if (index >= Length)
                return Array.Empty<byte>();

            int endIndex = index + length - 1;
            if (endIndex >= length)
            {
                endIndex = Length - 1;
            }

            int startChunkId = index / 31;
            int startChunkLoc = (index % 31) + 1;

            int endChunkId = endIndex / 31;
            int endChunkLoc = (endIndex % 31) + 1;

            byte[] codeSlice = new byte[(endIndex - index) + 1];
            Span<byte> codeSliceSpan = codeSlice;
            if (startChunkId == endChunkId)
            {
                WorldState.GetCodeChunk(Owner, (UInt256)startChunkId)[startChunkLoc..(endChunkLoc + 1)].CopyTo(codeSliceSpan);
            }
            else
            {
                WorldState.GetCodeChunk(Owner, (UInt256)startChunkId)[startChunkLoc..].CopyTo(codeSliceSpan);
                codeSliceSpan = codeSliceSpan.Slice(32 - startChunkLoc);
                for (int i = (startChunkId+1); i < endChunkId; i++)
                {
                    WorldState.GetCodeChunk(Owner, (UInt256)i)[1..].CopyTo(codeSliceSpan);
                    codeSliceSpan = codeSliceSpan.Slice(31);
                }
                WorldState.GetCodeChunk(Owner, (UInt256)endChunkId)[1..(endChunkLoc + 1)].CopyTo(codeSliceSpan);
            }
            return codeSlice;
        }
    }


    public class CodeInfo : IThreadPoolWorkItem
    {
        public ICode MachineCode { get; }
        public IPrecompile? Precompile { get; set; }
        private readonly JumpDestinationAnalyzer _analyzer;
        private static readonly JumpDestinationAnalyzer _emptyAnalyzer = new(Array.Empty<byte>());
        public static CodeInfo Empty { get; } = new CodeInfo(Array.Empty<byte>());

        public CodeInfo(byte[] code)
        {
            MachineCode = new ByteCode(code);
            _analyzer = code.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(code);
        }

        public CodeInfo(ReadOnlyMemory<byte> code)
        {
            MachineCode = new ByteCode(code.ToArray());
            _analyzer = code.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(code);
        }

        public CodeInfo(IWorldState worldState, Address codeOwner)
        {
            MachineCode = new VerkleCode(worldState, codeOwner);
            _analyzer = _emptyAnalyzer;
        }


        public bool IsPrecompile => Precompile is not null;

        public CodeInfo(IPrecompile precompile)
        {
            Precompile = precompile;
            MachineCode = new ByteCode(Array.Empty<byte>());
            _analyzer = _emptyAnalyzer;
        }

        public bool ValidateJump(int destination, bool isSubroutine)
        {
            return _analyzer.ValidateJump(destination, isSubroutine);
        }

        void IThreadPoolWorkItem.Execute()
        {
            _analyzer.Execute();
        }
    }
}
