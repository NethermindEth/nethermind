using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>Bench-only observations of rejected transactions during main-thread execution.</summary>
public static class RepairProbe
{
    [ThreadStatic] public static bool Armed;
    [ThreadStatic] public static Dictionary<StorageCell, UInt256>? Reads;
    [ThreadStatic] public static long PrefixGas;
    [ThreadStatic] public static long Ops;
    [ThreadStatic] public static long TaintedOps;
    [ThreadStatic] public static bool Unrepairable;
    [ThreadStatic] public static bool DifferingRead;
    [ThreadStatic] private static Dictionary<object, Frame>? _frames;
    [ThreadStatic] private static Frame? _current;
    [ThreadStatic] private static ulong _gasLimit;

    private sealed class Frame
    {
        public readonly bool[] Stack = new bool[EvmStack.MaxStackSize];
        public Frame? Parent;
        public bool All;
        public bool Memory;
        public bool ReturnData;
        public bool Call;
        public bool OpTainted;
        public bool Any;
        public int Output;
        public byte Opcode;
        public ulong Gas;
    }

    /// <summary>Starts observations for one main-thread transaction.</summary>
    public static void Begin(Dictionary<StorageCell, UInt256> reads, ulong gasLimit)
    {
        Reads = reads;
        _frames = [];
        _current = null;
        _gasLimit = gasLimit;
        PrefixGas = Ops = TaintedOps = 0;
        Unrepairable = DifferingRead = false;
        Armed = true;
    }

    /// <summary>Disarms observations and releases transaction references.</summary>
    public static void End()
    {
        Armed = false;
        Reads = null;
        _frames = null;
        _current = null;
    }

    /// <summary>Selects a new or resumed call frame.</summary>
    public static void Enter(object state, bool continuation, ulong gas, int head)
    {
        if (!continuation || !_frames!.TryGetValue(state, out Frame? frame))
        {
            frame = new() { Parent = _current, All = _current?.Call == true };
            _frames![state] = frame;
        }
        else if (head > 0)
        {
            frame.Stack[head - 1] = frame.Call || frame.ReturnData;
        }
        _current = frame;
        frame.Gas = gas;
    }

    /// <summary>Records the frame's remaining gas and conservatively propagates returned taint.</summary>
    public static void Leave(ulong gas, bool suspended)
    {
        Frame frame = _current!;
        frame.Gas = gas;
        if (suspended) return;
        if (frame.Parent is { } parent)
        {
            parent.ReturnData = frame.All || frame.Memory || frame.ReturnData || frame.Any;
            parent.Memory |= parent.ReturnData;
            _current = parent;
        }
    }

    /// <summary>Observes one generic dispatch step using a shadow stack.</summary>
    public static void Step(byte opcode, int head, ulong gas)
    {
        Frame frame = _current!;
        frame.Gas = gas;
        frame.Opcode = opcode;
        frame.Output = -1;
        frame.OpTainted = frame.All;
        Ops++;
        if (opcode is >= 0x80 and <= 0x8f)
        {
            int source = head - (opcode - 0x7f);
            if (source >= 0 && head < frame.Stack.Length)
                frame.Stack[head] = frame.All || frame.Stack[source];
            frame.OpTainted |= source >= 0 && frame.Stack[source];
        }
        else if (opcode is >= 0x90 and <= 0x9f)
        {
            int other = head - 1 - (opcode - 0x8f);
            if (other >= 0)
            {
                frame.OpTainted |= frame.Stack[head - 1] || frame.Stack[other];
                (frame.Stack[head - 1], frame.Stack[other]) = (frame.Stack[other], frame.Stack[head - 1]);
            }
        }
        else
        {
            (int inputs, int outputs) = Effect(opcode);
            bool taint = frame.All;
            for (int i = 0; i < inputs && i < head; i++) taint |= frame.Stack[head - 1 - i];
            if (opcode is 0x51 or 0x20) taint |= frame.Memory;
            if (opcode is 0x3d or 0x3e) taint |= frame.ReturnData;
            frame.OpTainted = taint;
            int shape = opcode switch
            {
                0x20 or 0xf3 or 0xfd => 2,
                0x37 or 0x39 or 0x3e or 0x5e => 3,
                0x3c => 4,
                0xf1 or 0xf2 => 7,
                0xf4 or 0xfa => 6,
                0xf0 => 3,
                0xf5 => 4,
                >= 0xa0 and <= 0xa4 => 2,
                0x51 or 0x52 or 0x53 or 0x54 or 0x55 or 0x56 or 0x57 or 0x5c or 0x5d => 1,
                _ => 0
            };
            for (int i = 0; i < shape && i < head; i++)
                Unrepairable |= frame.All || frame.Stack[head - 1 - i];
            if (opcode is 0x52 or 0x53 or 0x37 or 0x39 or 0x3c or 0x3e or 0x5e) frame.Memory |= taint;
            if (opcode is 0xf0 or 0xf1 or 0xf2 or 0xf4 or 0xf5 or 0xfa)
            {
                frame.Call = taint || frame.Memory;
                frame.ReturnData = frame.Call;
                frame.Memory |= frame.Call;
            }
            int output = head - inputs;
            if (output >= 0 && output < frame.Stack.Length && outputs > 0)
            {
                frame.Output = output;
                frame.Stack[output] = taint;
            }
        }
        frame.Any |= frame.OpTainted;
        if (frame.OpTainted) TaintedOps++;
    }

    /// <summary>Compares an actual persistent-storage read with the warm footprint.</summary>
    public static void Read(in StorageCell cell, in UInt256 value)
    {
        if (!Reads!.TryGetValue(cell, out UInt256 expected) || expected == value) return;
        Frame frame = _current!;
        if (!DifferingRead)
        {
            ulong remaining = 0;
            for (Frame? active = frame; active is not null; active = active.Parent) remaining += active.Gas;
            PrefixGas = remaining <= _gasLimit ? (long)(_gasLimit - remaining) : 0;
            DifferingRead = true;
        }
        frame.Any = true;
        if (!frame.OpTainted)
        {
            frame.OpTainted = true;
            TaintedOps++;
        }
        if (frame.Opcode == 0x54 && frame.Output >= 0) frame.Stack[frame.Output] = true;
    }

    private static (int Inputs, int Outputs) Effect(byte opcode) => opcode switch
    {
        >= 0x5f and <= 0x7f => (0, 1),
        0x08 or 0x09 => (3, 1),
        0x15 or 0x19 or 0x1e => (1, 1),
        >= 0x01 and <= 0x1d => (2, 1),
        0x20 => (2, 1),
        0x31 or 0x35 or 0x3b or 0x3f or 0x40 or 0x49 or 0x51 or 0x54 or 0x5c => (1, 1),
        0x37 or 0x39 or 0x3e or 0x5e => (3, 0),
        0x3c => (4, 0),
        >= 0x30 and <= 0x4b => (0, 1),
        0x50 or 0x56 or 0xff => (1, 0),
        0x52 or 0x53 or 0x55 or 0x57 or 0x5d or 0xf3 or 0xfd => (2, 0),
        0x58 or 0x59 or 0x5a => (0, 1),
        >= 0xa0 and <= 0xa4 => (2 + opcode - 0xa0, 0),
        0xf0 => (3, 1),
        0xf5 => (4, 1),
        0xf1 or 0xf2 => (7, 1),
        0xf4 or 0xfa => (6, 1),
        _ => (0, 0)
    };
}
