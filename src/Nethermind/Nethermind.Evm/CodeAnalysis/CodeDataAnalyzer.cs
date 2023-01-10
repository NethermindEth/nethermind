// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm.CodeAnalysis
{
    public class CodeDataAnalyzer : ICodeInfoAnalyzer
    {
        private byte[] _codeBitmap;
        private ReadOnlyMemory<byte> MachineCode { get; }

        public CodeDataAnalyzer(ReadOnlyMemory<byte> code)
        {
            MachineCode = code;
        }

        public bool ValidateJump(int destination, bool isSubroutine)
        {
            ReadOnlySpan<byte> machineCode = MachineCode.Span;
            _codeBitmap ??= BitmapHelper.CreateCodeBitmap(machineCode);

            if (destination < 0 || destination >= MachineCode.Length)
            {
                return false;
            }

            if (!BitmapHelper.IsCodeSegment(_codeBitmap, destination))
            {
                return false;
            }

            if (isSubroutine)
            {
                return machineCode[destination] == 0x5c;
            }

            return machineCode[destination] == 0x5b;
        }
    }
}
