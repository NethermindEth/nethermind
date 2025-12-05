// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm;
using Nethermind.Evm.Tracing;

namespace Nethermind.OpcodeTracing.Plugin.Tracing;

internal sealed class OpcodeCountingTxTracer : TxTracer
{
    private const int OpcodeSpace = 256;
    private readonly long[] _opcodeCounters = new long[OpcodeSpace];

    public override bool IsTracingInstructions => true;

    public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env, int codeSection = 0, int functionDepth = 0)
    {
        _opcodeCounters[(byte)opcode]++;
    }

    public void AccumulateInto(long[] aggregate)
    {
        for (int i = 0; i < OpcodeSpace; i++)
        {
            long value = _opcodeCounters[i];
            if (value == 0)
            {
                continue;
            }

            aggregate[i] += value;
        }
    }
}
