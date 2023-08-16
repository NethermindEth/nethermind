// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using DotNetty.Common.Utilities;
using Nethermind.Core;
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


    public class CodeInfo
    {
        private const int SampledCodeLength = 10_001;
        private const int PercentageOfPush1 = 40;
        private const int NumberOfSamples = 100;
        private static Random _rand = new();

        public ICode MachineCode { get; set; }
        public IPrecompile? Precompile { get; set; }
        private ICodeInfoAnalyzer? _analyzer;

        public CodeInfo(IWorldState worldState, Address codeOwner)
        {
            MachineCode = new VerkleCode(worldState, codeOwner);
        }

        public CodeInfo(byte[] code)
        {
            MachineCode = new ByteCode(code);
        }

        public bool IsPrecompile => Precompile is not null;

        public CodeInfo(IPrecompile precompile)
        {
            Precompile = precompile;
            MachineCode = new ByteCode(Array.Empty<byte>());
        }

        public bool ValidateJump(int destination, bool isSubroutine)
        {
            if (_analyzer is null)
            {
                CreateAnalyzer();
            }

            return _analyzer.ValidateJump(destination, isSubroutine);
        }

        /// <summary>
        /// Do sampling to choose an algo when the code is big enough.
        /// When the code size is small we can use the default analyzer.
        /// </summary>
        private void CreateAnalyzer()
        {
            if (MachineCode.Length >= SampledCodeLength)
            {
                byte push1Count = 0;

                // we check (by sampling randomly) how many PUSH1 instructions are in the code
                for (int i = 0; i < NumberOfSamples; i++)
                {
                    byte instruction = MachineCode[_rand.Next(0, MachineCode.Length)];

                    // PUSH1
                    if (instruction == 0x60)
                    {
                        push1Count++;
                    }
                }

                // If there are many PUSH1 ops then use the JUMPDEST analyzer.
                // The JumpdestAnalyzer can perform up to 40% better than the default Code Data Analyzer
                // in a scenario when the code consists only of PUSH1 instructions.
                _analyzer = push1Count > PercentageOfPush1 ? new JumpdestAnalyzer(MachineCode) : new CodeDataAnalyzer(MachineCode);
            }
            else
            {
                _analyzer = new CodeDataAnalyzer(MachineCode);
            }
        }
    }
}
