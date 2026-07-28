// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using System.Threading;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

internal static class OpcodeHistogram
{
    private static readonly long[] s_counts = new long[byte.MaxValue + 1];
    private static readonly long[] s_pairCounts = new long[(byte.MaxValue + 1) * (byte.MaxValue + 1)];
    private static readonly Timer s_flushTimer = new(static _ => Flush(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));

    [ThreadStatic]
    private static int t_previousOpcodePlusOne;

    static OpcodeHistogram() => AppDomain.CurrentDomain.ProcessExit += static (_, _) => Flush();

    public static void Record(Instruction instruction)
    {
        s_counts[(int)instruction]++;
        int previousPlusOne = t_previousOpcodePlusOne;
        if (previousPlusOne != 0)
            s_pairCounts[((previousPlusOne - 1) << 8) | (int)instruction]++;
        t_previousOpcodePlusOne = (int)instruction + 1;
    }

    public static void Record(in StreamOp entry)
    {
        if (entry.Kind is StreamOpKind.StaticJump or StreamOpKind.StaticJumpI)
        {
            Record(Instruction.PUSH2);
            Record(entry.Kind == StreamOpKind.StaticJump ? Instruction.JUMP : Instruction.JUMPI);
            return;
        }

        if (entry.Kind is StreamOpKind.FusedBlockFirst or StreamOpKind.FusedInBlock)
        {
            int pushWidth = entry.Advance - 2;
            Record((Instruction)((int)Instruction.PUSH1 + pushWidth - 1));
            Record(MapFused(entry.Opcode));
            return;
        }

        Record((Instruction)entry.Opcode);
    }

    private static Instruction MapFused(byte opcode) => opcode switch
    {
        FusedOpcode.Add => Instruction.ADD,
        FusedOpcode.Sub => Instruction.SUB,
        FusedOpcode.Mul => Instruction.MUL,
        FusedOpcode.Div => Instruction.DIV,
        FusedOpcode.SDiv => Instruction.SDIV,
        FusedOpcode.Mod => Instruction.MOD,
        FusedOpcode.SMod => Instruction.SMOD,
        FusedOpcode.Lt => Instruction.LT,
        FusedOpcode.Gt => Instruction.GT,
        FusedOpcode.SLt => Instruction.SLT,
        FusedOpcode.SGt => Instruction.SGT,
        FusedOpcode.Eq => Instruction.EQ,
        FusedOpcode.And => Instruction.AND,
        FusedOpcode.Or => Instruction.OR,
        FusedOpcode.Xor => Instruction.XOR,
        FusedOpcode.Shl => Instruction.SHL,
        FusedOpcode.Shr => Instruction.SHR,
        _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, null),
    };

    private static void Flush()
    {
        try
        {
            Console.Error.Write(BuildReport());
        }
        catch (Exception)
        {
        }
    }

    private static string BuildReport()
    {
        long total = 0;
        for (int i = 0; i < s_counts.Length; i++)
            total += s_counts[i];

        int[] order = new int[s_counts.Length];
        for (int i = 0; i < order.Length; i++)
            order[i] = i;
        Array.Sort(order, static (a, b) => s_counts[b].CompareTo(s_counts[a]));

        StringBuilder report = new();
        report.AppendLine($"[OPDIAG] total={total:N0}");
        foreach (int opcode in order)
        {
            long count = s_counts[opcode];
            if (count == 0)
                continue;
            report.AppendLine($"[OPDIAG] {(Instruction)opcode,-18} {count,16:N0} {(double)count / total,8:P2}");
        }

        int[] pairOrder = new int[s_pairCounts.Length];
        for (int i = 0; i < pairOrder.Length; i++)
            pairOrder[i] = i;
        Array.Sort(pairOrder, static (a, b) => s_pairCounts[b].CompareTo(s_pairCounts[a]));

        report.AppendLine("[OPDIAG] top-pairs");
        for (int rank = 0; rank < 80; rank++)
        {
            int pair = pairOrder[rank];
            long count = s_pairCounts[pair];
            if (count == 0)
                break;
            report.AppendLine(
                $"[OPDIAG] {(Instruction)(pair >> 8)} -> {(Instruction)(pair & 0xFF),-18} {count,16:N0} {(double)count / total,8:P2}");
        }

        return report.ToString();
    }
}
