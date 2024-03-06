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
    public class CodeInfo : IThreadPoolWorkItem
    {
        public ReadOnlyMemory<byte> MachineCode { get; }
        public IPrecompile? Precompile { get; set; }
        private readonly JumpDestinationAnalyzer _analyzer;
        private static readonly JumpDestinationAnalyzer _emptyAnalyzer = new(Array.Empty<byte>());
        public static CodeInfo Empty { get; } = new CodeInfo(Array.Empty<byte>());

        public CodeInfo(byte[] code)
        {
            MachineCode = code;
            _analyzer = code.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(code);
        }

        public CodeInfo(ReadOnlyMemory<byte> code)
        {
            MachineCode = code.ToArray();
            _analyzer = code.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(code);
        }

        public CodeInfo(IWorldState worldState, Address codeOwner)
        {
            var verkleWorldState = worldState as VerkleWorldState;
            MachineCode = RebuildCodeFromChunks(verkleWorldState!, codeOwner);
            _analyzer = MachineCode.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(MachineCode);
        }


        public bool IsPrecompile => Precompile is not null;

        public CodeInfo(IPrecompile precompile)
        {
            Precompile = precompile;
            MachineCode = Array.Empty<byte>();
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

        private static byte[] RebuildCodeFromChunks(VerkleWorldState worldState, Address owner)
        {
            int length = (int)worldState.GetAccount(owner).CodeSize;

            if (0 >= length)
                return Array.Empty<byte>();

            int endIndex = length - 1;

            int endChunkId = endIndex / 31;
            int endChunkLoc = (endIndex % 31) + 1;

            byte[] codeSlice = new byte[endIndex  + 1];
            Span<byte> codeSliceSpan = codeSlice;
            if (0 == endChunkId)
            {
                worldState.GetCodeChunkOrEmpty(owner, (UInt256)0)[1..(endChunkLoc + 1)].CopyTo(codeSliceSpan);
            }
            else
            {
                worldState.GetCodeChunkOrEmpty(owner, (UInt256)0)[1..].CopyTo(codeSliceSpan);
                codeSliceSpan = codeSliceSpan[31..];
                for (int i = 1; i < endChunkId; i++)
                {
                    worldState.GetCodeChunkOrEmpty(owner, (UInt256)i)[1..].CopyTo(codeSliceSpan);
                    codeSliceSpan = codeSliceSpan[31..];
                }
                worldState.GetCodeChunkOrEmpty(owner, (UInt256)endChunkId)[1..(endChunkLoc + 1)].CopyTo(codeSliceSpan);
            }
            return codeSlice;
        }
    }
}
