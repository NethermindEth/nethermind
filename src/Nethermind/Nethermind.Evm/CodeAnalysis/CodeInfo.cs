// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis
{
    public class CodeInfo
    {
        private static int ILAnalyzerTreshold = 23;


        private const int SampledCodeLength = 10_001;
        private const int PercentageOfPush1 = 40;
        private const int NumberOfSamples = 100;
        private static Random _rand = new();

        private ILInfo? _iLInfo;
        public byte[] MachineCode { get; set; }
        public IPrecompile? Precompile { get; set; }
        private ICodeInfoAnalyzer? _jumpAnalyzer;
        private ILAnalizer? _ilAnalyzer;

        public int callCount = 0;
        public int CallCount {
            get => callCount;
            set
            {
                callCount = value;
                if(callCount  > ILAnalyzerTreshold)
                {
                    _iLInfo = _ilAnalyzer.Analyze(MachineCode);
                }
            }
        } 

        public CodeInfo(byte[] code)
        {
            MachineCode = code;
        }

        public bool IsPrecompile => Precompile is not null;
        public bool IsILed => _iLInfo is not null;

        public CodeInfo(IPrecompile precompile)
        {
            Precompile = precompile;
            MachineCode = Array.Empty<byte>();
        }

        public bool ValidateJump(int destination, bool isSubroutine)
        {
            if (_jumpAnalyzer is null)
            {
                CreateAnalyzer();
            }

            return _jumpAnalyzer.ValidateJump(destination, isSubroutine);
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
                _jumpAnalyzer = push1Count > PercentageOfPush1 ? new JumpdestAnalyzer(MachineCode) : new CodeDataAnalyzer(MachineCode);
            }
            else
            {
                _jumpAnalyzer = new CodeDataAnalyzer(MachineCode);
            }
        }
    }
}
