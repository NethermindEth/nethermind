// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Follows the values a run loads from storage through the stack, to find the slots whose value at the transaction's
/// start the run only adds to or subtracts from and compares: for any start value that satisfies the same comparisons,
/// the run is the same, with that slot's writes moved by the same amount.
/// </summary>
/// <remarks>
/// A loaded value is followed as its slot's start value plus an offset. Adding or subtracting a value that does not
/// derive from storage moves the offset; a comparison with such a value, or with the same slot's value at another
/// offset, becomes a bound or an excluded start value; a store of it to its own slot is a delta. Any other use of a
/// followed value, a store of anything else to its slot, or a revert after a delta was stored gives the slot up.
/// Bounds keep every followed value from wrapping, and stores keep each value's zero-ness, which the storage gas and
/// refunds depend on.
/// </remarks>
internal sealed class StorageDeltaTracer : FootprintRecorder.OutcomeTracer
{
    private static readonly BigInteger Max = (BigInteger.One << 256) - 1;

    private readonly List<Frame> _frames = [];
    private readonly Dictionary<StorageCell, int> _bases = [];
    private readonly List<Base> _states = [];
    private Instruction _op;
    private int _depth;
    private Address? _account;
    private bool _broken;
    private bool _deltaStored;

    public StorageDeltaTracer()
    {
        IsTracingInstructions = true;
        IsTracingStack = true;
    }

    public override void Reset(IBlockProcessingProgress? progress = null, int txIndex = 0, System.Threading.CancellationToken token = default)
    {
        base.Reset(progress, txIndex, token);
        _frames.Clear();
        _bases.Clear();
        _states.Clear();
        _broken = false;
        _deltaStored = false;
    }

    /// <summary>The slot's start value bounds and the values it must not start at, when the run is such for it.</summary>
    public bool TryGetDelta(in StorageCell cell, in UInt256 startValue, out SlotDelta delta)
    {
        delta = default;
        if (_broken || !_bases.TryGetValue(cell, out int id)) return false;
        Base state = _states[id];
        if (state.GivenUp || !state.ObservedKnown || state.Observed != ToBig(startValue)) return false;
        if (state.Lo > state.Hi || state.Lo < 0 || state.Hi > Max || state.Observed < state.Lo || state.Observed > state.Hi) return false;
        List<UInt256>? excluded = null;
        foreach (BigInteger value in state.Excluded)
        {
            if (value < state.Lo || value > state.Hi) continue;
            if (value == state.Observed) return false;
            (excluded ??= []).Add(ToUInt256(value));
        }

        delta = new SlotDelta(ToUInt256(state.Lo), ToUInt256(state.Hi), excluded?.ToArray() ?? [], state.Written ? state.Offset : BigInteger.Zero);
        return true;
    }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        _op = opcode;
        _depth = env.CallDepth;
        _account = env.ExecutingAccount;
    }

    public override void ReportOperationError(EvmExceptionType error)
    {
        // A frame that fails undoes its stores, which a delta does not follow.
        if (_deltaStored) _broken = true;
    }

    public override void SetOperationStack(TraceStack stack)
    {
        if (_broken) return;
        // A frame that returned is gone; a new one starts with an empty stack.
        if (_frames.Count > _depth + 1) _frames.RemoveRange(_depth + 1, _frames.Count - _depth - 1);
        while (_frames.Count < _depth + 1) _frames.Add(new Frame());
        Frame frame = _frames[_depth];
        if (frame.Pending) Finish(frame, stack);
        if (_broken) return;
        if (frame.Taints.Count != stack.Count)
        {
            _broken = true;
            return;
        }

        Begin(frame, stack);
    }

    private void Begin(Frame frame, TraceStack stack)
    {
        Instruction op = _op;
        if (!TryGetStackEffect(op, out int pops, out int pushes))
        {
            _broken = true;
            return;
        }

        if (stack.Count < pops)
        {
            _broken = true;
            return;
        }

        List<Taint> taints = frame.Taints;
        int top = taints.Count - 1;
        frame.Pending = true;
        frame.Pops = pops;
        frame.Pushes = pushes;
        frame.Out0 = Taint.None;
        frame.Out1 = Taint.None;
        frame.LoadedCell = null;
        frame.Op = op;

        bool anyTainted = false;
        for (int i = 0; i < pops; i++) anyTainted |= taints[top - i].IsTainted;

        switch (op)
        {
            case >= Instruction.DUP1 and <= Instruction.DUP16:
                frame.Out0 = taints[top - (op - Instruction.DUP1)];
                return;
            case >= Instruction.SWAP1 and <= Instruction.SWAP16:
                frame.Out0 = taints[top];
                frame.Out1 = taints[top - (op - Instruction.SWAP1 + 1)];
                return;
            case Instruction.POP:
                return;
            case Instruction.SLOAD:
                if (taints[top].IsTainted)
                {
                    GiveUp(taints[top]);
                    return;
                }

                LoadFrom(frame, new StorageCell(_account!, stack.PeekUInt256(0)));
                return;
            case Instruction.SSTORE:
                Store(taints[top], taints[top - 1], new StorageCell(_account!, stack.PeekUInt256(0)));
                return;
        }

        if (!anyTainted)
        {
            if (op is Instruction.REVERT or Instruction.SELFDESTRUCT && _deltaStored) _broken = true;
            return;
        }

        Taint a = taints[top];
        Taint b = pops > 1 ? taints[top - 1] : Taint.None;
        switch (op)
        {
            case Instruction.ADD when a.IsTainted != b.IsTainted:
                frame.Out0 = a.IsTainted ? a.Move(ToBig(stack.PeekUInt256(1))) : b.Move(ToBig(stack.PeekUInt256(0)));
                return;
            case Instruction.SUB when a.IsTainted && !b.IsTainted:
                frame.Out0 = a.Move(-ToBig(stack.PeekUInt256(1)));
                return;
            case Instruction.LT or Instruction.GT or Instruction.EQ:
                Compare(op, a, b, ToBig(stack.PeekUInt256(0)), ToBig(stack.PeekUInt256(1)));
                return;
            case Instruction.ISZERO:
                Compare(Instruction.EQ, a, Taint.None, ToBig(stack.PeekUInt256(0)), BigInteger.Zero);
                return;
        }

        for (int i = 0; i < pops; i++) GiveUp(taints[top - i]);
    }

    private void Finish(Frame frame, TraceStack stack)
    {
        frame.Pending = false;
        List<Taint> taints = frame.Taints;
        int pops = frame.Pops;
        int pushes = frame.Pushes;
        if (taints.Count < pops)
        {
            _broken = true;
            return;
        }

        if (frame.Op is >= Instruction.SWAP1 and <= Instruction.SWAP16)
        {
            int depth = frame.Op - Instruction.SWAP1 + 1;
            taints[^1] = frame.Out1;
            taints[^(depth + 1)] = frame.Out0;
            return;
        }

        if (frame.Op is >= Instruction.DUP1 and <= Instruction.DUP16)
        {
            taints.Add(frame.Out0);
            return;
        }

        taints.RemoveRange(taints.Count - pops, pops);
        for (int i = 0; i < pushes; i++) taints.Add(i == 0 ? frame.Out0 : Taint.None);

        if (frame.LoadedCell is { } cell && stack.Count > 0 && _bases.TryGetValue(cell, out int id))
        {
            // The value the load returned: the start value of a slot loaded for the first time, or its start value
            // moved by what the run stored since.
            Base state = _states[id];
            BigInteger loaded = ToBig(stack.PeekUInt256(0));
            if (!state.ObservedKnown)
            {
                state.Observed = loaded;
                state.ObservedKnown = true;
            }
            else if (Wrap(state.Observed + state.Offset) != loaded)
            {
                state.GivenUp = true;
            }
        }
    }

    private void LoadFrom(Frame frame, in StorageCell cell)
    {
        if (!_bases.TryGetValue(cell, out int id))
        {
            id = _states.Count;
            _states.Add(new Base());
            _bases[cell] = id;
        }

        Base state = _states[id];
        frame.LoadedCell = cell;
        if (!state.GivenUp) frame.Out0 = new Taint(id, state.Offset);
    }

    private void Store(Taint key, Taint value, in StorageCell cell)
    {
        if (key.IsTainted) GiveUp(key);
        bool isBase = _bases.TryGetValue(cell, out int id);
        if (!value.IsTainted || !isBase || value.Base != id)
        {
            // Another value stored to a followed slot, or a followed value to another slot.
            if (value.IsTainted) GiveUp(value);
            if (isBase) _states[id].GivenUp = true;
            else
            {
                // A slot stored before it is loaded is never followed.
                _bases[cell] = _states.Count;
                _states.Add(new Base { GivenUp = true });
            }

            return;
        }

        Base state = _states[id];
        if (state.GivenUp || !state.ObservedKnown) return;
        // The gas and the refund of a store depend on whether the original, current and new values are zero.
        KeepsZeroness(state, BigInteger.Zero);
        KeepsZeroness(state, state.Offset);
        KeepsZeroness(state, value.Offset);
        state.Offset = value.Offset;
        state.Written = true;
        _deltaStored = true;
    }

    private static void KeepsZeroness(Base state, BigInteger offset)
    {
        NoWrap(state, offset);
        if (state.Observed + offset == 0)
        {
            state.GivenUp = true;
            return;
        }

        state.Excluded.Add(-offset);
    }

    private void Compare(Instruction op, Taint a, Taint b, BigInteger aValue, BigInteger bValue)
    {
        if (a.IsTainted && b.IsTainted)
        {
            if (a.Base != b.Base)
            {
                GiveUp(a);
                GiveUp(b);
                return;
            }

            // Without wrapping, the outcome depends on the offsets only.
            Base same = _states[a.Base];
            NoWrap(same, a.Offset);
            NoWrap(same, b.Offset);
            return;
        }

        bool aTainted = a.IsTainted;
        Taint taint = aTainted ? a : b;
        Base state = _states[taint.Base];
        if (state.GivenUp) return;
        if (!state.ObservedKnown)
        {
            state.GivenUp = true;
            return;
        }

        NoWrap(state, taint.Offset);
        BigInteger constant = aTainted ? bValue : aValue;
        // The start value the comparison turns on: the followed value is the start value plus the offset.
        BigInteger pivot = constant - taint.Offset;
        BigInteger start = state.Observed;
        switch (op)
        {
            case Instruction.EQ:
                if (start == pivot)
                {
                    state.Lo = BigInteger.Max(state.Lo, pivot);
                    state.Hi = BigInteger.Min(state.Hi, pivot);
                }
                else
                {
                    state.Excluded.Add(pivot);
                }

                return;
            // followed < constant, or constant < followed
            case Instruction.LT when aTainted:
            case Instruction.GT when !aTainted:
                if (start < pivot) state.Hi = BigInteger.Min(state.Hi, pivot - 1);
                else state.Lo = BigInteger.Max(state.Lo, pivot);
                return;
            default:
                // followed > constant
                if (start > pivot) state.Lo = BigInteger.Max(state.Lo, pivot + 1);
                else state.Hi = BigInteger.Min(state.Hi, pivot);
                return;
        }
    }

    private static void NoWrap(Base state, BigInteger offset)
    {
        state.Lo = BigInteger.Max(state.Lo, -offset);
        state.Hi = BigInteger.Min(state.Hi, Max - offset);
    }

    private void GiveUp(Taint taint)
    {
        if (taint.IsTainted) _states[taint.Base].GivenUp = true;
    }

    private static BigInteger Wrap(BigInteger value)
    {
        BigInteger wrapped = value & Max;
        return wrapped.Sign < 0 ? wrapped + Max + 1 : wrapped;
    }

    private static BigInteger ToBig(in UInt256 value) => (BigInteger)value;

    private static UInt256 ToUInt256(BigInteger value) => (UInt256)value;

    /// <summary>How many words the instruction pops and pushes; false for one this tracer does not model.</summary>
    internal static bool TryGetStackEffect(Instruction op, out int pops, out int pushes)
    {
        (pops, pushes) = op switch
        {
            Instruction.STOP or Instruction.JUMPDEST or Instruction.INVALID => (0, 0),
            Instruction.ADD or Instruction.MUL or Instruction.SUB or Instruction.DIV or Instruction.SDIV or Instruction.MOD
                or Instruction.SMOD or Instruction.EXP or Instruction.SIGNEXTEND or Instruction.LT or Instruction.GT
                or Instruction.SLT or Instruction.SGT or Instruction.EQ or Instruction.AND or Instruction.OR or Instruction.XOR
                or Instruction.BYTE or Instruction.SHL or Instruction.SHR or Instruction.SAR or Instruction.KECCAK256 => (2, 1),
            Instruction.ADDMOD or Instruction.MULMOD => (3, 1),
            Instruction.ISZERO or Instruction.NOT or Instruction.CLZ or Instruction.BALANCE or Instruction.CALLDATALOAD
                or Instruction.EXTCODESIZE or Instruction.EXTCODEHASH or Instruction.BLOCKHASH or Instruction.BLOBHASH
                or Instruction.MLOAD or Instruction.SLOAD or Instruction.TLOAD => (1, 1),
            Instruction.ADDRESS or Instruction.ORIGIN or Instruction.CALLER or Instruction.CALLVALUE or Instruction.CALLDATASIZE
                or Instruction.CODESIZE or Instruction.GASPRICE or Instruction.RETURNDATASIZE or Instruction.COINBASE
                or Instruction.TIMESTAMP or Instruction.NUMBER or Instruction.PREVRANDAO or Instruction.GASLIMIT
                or Instruction.CHAINID or Instruction.SELFBALANCE or Instruction.BASEFEE or Instruction.BLOBBASEFEE
                or Instruction.SLOTNUM or Instruction.PC or Instruction.MSIZE or Instruction.GAS => (0, 1),
            Instruction.CALLDATACOPY or Instruction.CODECOPY or Instruction.RETURNDATACOPY or Instruction.MCOPY => (3, 0),
            Instruction.EXTCODECOPY => (4, 0),
            Instruction.POP or Instruction.JUMP or Instruction.SELFDESTRUCT => (1, 0),
            Instruction.MSTORE or Instruction.MSTORE8 or Instruction.SSTORE or Instruction.JUMPI or Instruction.TSTORE
                or Instruction.RETURN or Instruction.REVERT => (2, 0),
            >= Instruction.PUSH0 and <= Instruction.PUSH32 => (0, 1),
            >= Instruction.DUP1 and <= Instruction.DUP16 => (op - Instruction.DUP1 + 1, op - Instruction.DUP1 + 2),
            >= Instruction.SWAP1 and <= Instruction.SWAP16 => (op - Instruction.SWAP1 + 2, op - Instruction.SWAP1 + 2),
            >= Instruction.LOG0 and <= Instruction.LOG4 => (op - Instruction.LOG0 + 2, 0),
            Instruction.CREATE => (3, 1),
            Instruction.CREATE2 => (4, 1),
            Instruction.CALL or Instruction.CALLCODE => (7, 1),
            Instruction.DELEGATECALL or Instruction.STATICCALL => (6, 1),
            _ => (-1, -1)
        };
        return pops >= 0;
    }

    // A followed value: the start value of the slot with id Base, plus Offset.
    private readonly record struct Taint(int Base, BigInteger Offset)
    {
        public static readonly Taint None = new(-1, BigInteger.Zero);

        public bool IsTainted => Base >= 0;

        public Taint Move(BigInteger by) => this with { Offset = Offset + by };
    }

    private sealed class Base
    {
        public BigInteger Observed;
        public bool ObservedKnown;
        public BigInteger Lo = BigInteger.Zero;
        public BigInteger Hi = Max;
        public readonly List<BigInteger> Excluded = [];
        // What the run's last store left the slot at, relative to its start value.
        public BigInteger Offset = BigInteger.Zero;
        public bool Written;
        public bool GivenUp;
    }

    private sealed class Frame
    {
        public readonly List<Taint> Taints = [];
        public bool Pending;
        public Instruction Op;
        public int Pops;
        public int Pushes;
        public Taint Out0 = Taint.None;
        public Taint Out1 = Taint.None;
        public StorageCell? LoadedCell;
    }
}

/// <summary>
/// A slot whose start value a run only adds to, subtracts from and compares: it holds for any start value in
/// [<see cref="Lo"/>, <see cref="Hi"/>] other than <see cref="Excluded"/>, and moves the slot by <see cref="Delta"/>.
/// </summary>
internal readonly record struct SlotDelta(UInt256 Lo, UInt256 Hi, UInt256[] Excluded, BigInteger Delta);
