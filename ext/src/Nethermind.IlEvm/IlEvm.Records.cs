// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.IlEvm;

public static unsafe partial class IlEvm
{
    /// <summary>What the Legacy rules remember of a code by its content, whichever code infos carry it.</summary>
    /// <remarks>
    /// A state override's code gets a new code info whenever the core's override cache lost it, and with it a new code
    /// state: what is kept here outlives them. A content hash collision only shares a record between two codes.
    /// </remarks>
    internal sealed class IlCodeRecord(long content, int length)
    {
        public readonly long Content = content;
        public readonly int Length = length;

        /// <summary>The fork the code's last compile failed for, and why; a code state of the code takes them instead of compiling.</summary>
        /// <remarks>Written reason first, read fork first, so a reader that sees the fork sees its reason.</remarks>
        public volatile IlFork? FailedFor;
        public string? FailureReason;

        public void Failed(IlFork fork, string? reason)
        {
            FailureReason = reason;
            FailedFor = fork;
        }

        /// <summary>
        /// For a request's code, its hotness over every code info that carried it (<see cref="HotByContent"/>): the frames
        /// counted, the opcodes the interpreter ran, and the one-second window of the rate rule, as a code state keeps them.
        /// </summary>
        /// <remarks>Written racily, as a code state's counters are: a lost count only delays the compile.</remarks>
        public long Frames;
        public long OpCodesRun;
        public long WindowStart;
        public int WindowOpCodes;
        public int WindowFrames;

        /// <summary>Set while a layout of the code waits or runs, so that its other code infos do not queue it again.</summary>
        public int Pending;

        /// <summary>Adds the opcodes the interpreter ran of one of the code's frames, as <see cref="OnInterpreted"/> adds them to the frame's code state.</summary>
        public void Ran(long opCodes, long now)
        {
            OpCodesRun += opCodes;
            if (now - WindowStart >= 1000)
            {
                WindowStart = now;
                WindowOpCodes = 0;
                WindowFrames = 0;
            }

            WindowOpCodes = (int)Math.Min(int.MaxValue, WindowOpCodes + opCodes);
            WindowFrames++;
        }

        /// <summary>For a request's code: the distinct requests counted so far, up to <see cref="RequestsToCompile"/>, and which.</summary>
        private int _requests;
        private readonly int[] _seen = new int[RequestsToCompile];

        /// <summary>Counts <paramref name="request"/> unless counted already; true once <see cref="RequestsToCompile"/> distinct requests ran the code.</summary>
        /// <remarks>Racy: two requests counted at once may count as one, which only delays the compile.</remarks>
        public bool CountRequest(int request)
        {
            int counted = Volatile.Read(ref _requests);
            if (counted >= RequestsToCompile)
                return true;
            for (int i = 0; i < counted; i++)
            {
                if (_seen[i] == request)
                    return false;
            }

            _seen[counted] = request;
            Volatile.Write(ref _requests, counted + 1);
            return counted + 1 >= RequestsToCompile;
        }
    }

    /// <summary>The distinct requests that must run a request's code before it is compiled.</summary>
    internal const int RequestsToCompile = 4;

    /// <summary>
    /// The chain's code database, which the startup step hands the Legacy rules: code a cache stamped that it does not
    /// hold, as code a request deployed with a CREATE, came with the request. Null where no startup step ran: stamped code
    /// then counts as the chain's.
    /// </summary>
    internal static IReadOnlyKeyValueStore? ChainCode;

    private const byte FromChain = 1;
    private const byte FromRequest = 2;

    /// <summary>
    /// Whether the code of <paramref name="state"/> came with a request: no cache stamped it, as none stamps a state
    /// override's, or the chain's code database does not hold it; looked up once per code state.
    /// </summary>
    internal static bool IsRequestCode(CodeInfo codeInfo, IlCodeState state)
    {
        byte origin = state.Origin;
        if (origin == 0)
        {
            ValueHash256 hash = codeInfo.CodeHash;
            origin = hash == default || (ChainCode is { } chainCode && !chainCode.KeyExists(hash.Bytes)) ? FromRequest : FromChain;
            state.Origin = origin;
        }

        return origin == FromRequest;
    }

    /// <summary>
    /// The request a query's frame runs for: the identities of the tracer its machine runs under and of the block header
    /// it runs on. An eth_call clones its header and builds its tracer, eth_estimateGas keeps one of each for all its
    /// runs, and a simulation builds its blocks' headers while its tracer may be shared.
    /// </summary>
    /// <remarks>Hashes, so nothing is kept alive; two requests that share both only count as one.</remarks>
    private static int RequestOf(VirtualMachine<EthereumGasPolicy> query) =>
        RuntimeHelpers.GetHashCode(query.TxTracer) * 31 + RuntimeHelpers.GetHashCode(query.BlockExecutionContext.Header);

    /// <summary>
    /// Whether the code of <paramref name="state"/>, which a query brought, is hot: by its content, over every code info
    /// that carried it, and only across requests. The frame's <paramref name="frames"/> and its request are counted first;
    /// the code is hot once a code state's thresholds hold for its content and <see cref="RequestsToCompile"/> distinct
    /// requests ran it. A state override's code gets a new code info whenever the core's override cache lost it, so its
    /// code states alone may never see enough of it; and one request, however often it repeats a frame, never suffices.
    /// </summary>
    private static bool HotByContent(CodeInfo codeInfo, IlCodeState state, VirtualMachine<EthereumGasPolicy> query, int frames)
    {
        IlCodeRecord record = RecordFor(state.ContentHash, codeInfo.CodeSpan.Length);
        record.Frames += frames;
        return record.CountRequest(RequestOf(query)) &&
            (record.Frames >= IlEvmSettings.Threshold || record.OpCodesRun >= IlEvmSettings.OpcodeThreshold) &&
            record.WindowOpCodes >= IlEvmSettings.HotOpcodesPerSecond && (SamplingWeight == 1 || record.WindowFrames >= 2);
    }

    /// <summary>The records' table: direct-mapped, so it never holds more than its slots.</summary>
    /// <remarks>
    /// A code takes the slot its content hash picks, over whatever code had it. The hash is seeded per process, so no
    /// caller can aim codes at a slot, and churning through many evicts any one record about once per 4,096 codes made.
    /// </remarks>
    private static readonly IlCodeRecord?[] _records = new IlCodeRecord?[4_096];

    /// <summary>Forgets every record, for tests: a failure under one test's settings is not the next test's.</summary>
    internal static void ClearRecords() => System.Array.Clear(_records);

    /// <summary>The record of the code of <paramref name="content"/> hash and <paramref name="length"/>, or null for none.</summary>
    internal static IlCodeRecord? RecordOf(long content, int length)
    {
        IlCodeRecord? record = Volatile.Read(ref _records[(int)content & (_records.Length - 1)]);
        return record is not null && record.Content == content && record.Length == length ? record : null;
    }

    /// <summary>The code's record, made in its slot if another code holds it or none.</summary>
    /// <remarks>Racing threads may each make one, and the later replaces the earlier with what it held.</remarks>
    private static IlCodeRecord RecordFor(long content, int length)
    {
        ref IlCodeRecord? slot = ref _records[(int)content & (_records.Length - 1)];
        IlCodeRecord? record = Volatile.Read(ref slot);
        if (record is not null && record.Content == content && record.Length == length)
            return record;
        IlCodeRecord created = new(content, length);
        Volatile.Write(ref slot, created);
        return created;
    }

    /// <summary>Whether a compile of the code of <paramref name="state"/> failed for <paramref name="fork"/> under any code info; if so the state takes the failure.</summary>
    private static bool TakeKnownFailure(IlCodeState state, int length, IlFork fork)
    {
        if (!state.HasContentHash || RecordOf(state.ContentHash, length) is not { } record || !ReferenceEquals(record.FailedFor, fork))
            return false;
        state.FailureReason = record.FailureReason;
        state.FailedFor = fork;
        return true;
    }
}
