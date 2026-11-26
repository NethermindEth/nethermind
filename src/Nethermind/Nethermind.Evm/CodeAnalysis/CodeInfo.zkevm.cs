// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Evm.CodeAnalysis;

public sealed partial class CodeInfo
{
    public static CodeInfo Empty => field ??= new CodeInfo(_emptyAnalyzer);
    private static JumpDestinationAnalyzer _emptyAnalyzer => field ??= new JumpDestinationAnalyzer(new CodeInfo(), skipAnalysis: true);

    private ReadOnlyMemory<byte> _code;

    /// <remarks>
    /// Copies unless the code is an <see cref="ExecutableCodeMemory"/> buffer, or code it placed in the input a guest
    /// reuses, as any other buffer promises nothing about the bytes after the code. Padding here rather than on first
    /// execution keeps <see cref="Code"/> a plain field read, which the guest pays for on every code access.
    /// </remarks>
    partial void InitializeCode(ReadOnlyMemory<byte> code) =>
        _code = code.IsEmpty ? code
            : ExecutableCodeMemory.TryGetExecutionBuffer(code, out byte[]? buffer) ? buffer.AsMemory(0, code.Length)
            : OpenVmFlag.IsActive && ExecutableCodeMemory.TryGetInputBuffer(code, out byte[]? inputBuffer) ? inputBuffer.AsMemory(0, code.Length)
            : (FirstTouchMemoryFlag.IsActive || AirbenderFlag.IsActive) && ExecutableCodeMemory.IsPlaced(code) ? code
            : CreatePaddedCode(code.Span).AsMemory(0, code.Length);

    /// <summary>The buffers that the creation frames at each call depth run their init code from.</summary>
    private static InitCodeBuffers[]? _initCodeBuffers;

    /// <summary>A bitmap to clear and take as <see cref="IncrementalJumpBitmap"/> instead of allocating one, or <see langword="null"/>.</summary>
    private readonly long[]? _jumpBitmapBuffer;

    /// <summary>Wraps the first <paramref name="codeLength"/> bytes of <paramref name="executionBuffer"/>, followed by the execution padding, as code to run in place.</summary>
    private CodeInfo(byte[] executionBuffer, int codeLength, long[] jumpBitmapBuffer)
    {
        _code = executionBuffer.AsMemory(0, codeLength);
        _jumpBitmapBuffer = jumpBitmapBuffer;
        _analyzer = new JumpDestinationAnalyzer(this);
    }

    /// <remarks>
    /// The guest never frees memory, and a block can run thousands of creations of large init code for little gas: a copy
    /// of each and of its jump bitmap took the whole heap in the benchmark blocks of tests-zkevm-benchmark v21.0.7. Each
    /// call depth runs one frame at a time, and nothing reads the code of a creation frame once it returns, so a creation
    /// takes over the buffers of the last one at its depth. Their capacities are powers of two, so the code buffers a depth
    /// allocates add up to less than twice the largest capacity, which is less than four times the largest init code run
    /// at that depth. Each buffer also holds the execution padding and comes with a bitmap of a bit per byte of its capacity.
    /// </remarks>
    internal static partial CodeInfo ForInitCode(ReadOnlyMemory<byte> initCode, int callDepth)
    {
        int length = initCode.Length;
        if (length == 0) return new CodeInfo(initCode);

        ref InitCodeBuffers buffers = ref GetInitCodeBuffers(callDepth, length);
        byte[] code = buffers.Code!;
        ReadOnlySpan<byte> source = initCode.Span;
        // Where memory is swizzled, TryLoadInitCode has loaded the init code into the buffer already.
        if (!EvmPooledMemory.IsSwizzled || !Unsafe.AreSame(ref MemoryMarshal.GetReference(source), ref MemoryMarshal.GetArrayDataReference(code)))
            source.CopyTo(code);

        // Longer init code that ran before at this depth may have left its bytes there.
        code.AsSpan(length, ExecutionPadding).Clear();
        return new CodeInfo(code, length, buffers.JumpBitmap!);
    }

    /// <remarks>
    /// The guest never frees memory, so the init code is copied once, into the buffer <see cref="ForInitCode"/> runs it
    /// from. Memory in natural byte order is borrowed: nothing writes the creating frame's memory before
    /// <see cref="ForInitCode"/> copies it, and the guest's only action tracer keeps none of it. Swizzled memory is read
    /// into that buffer in natural order here instead, rather than into a copy of its own.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static partial bool TryLoadInitCode(ref EvmPooledMemory memory, in UInt256 location, in UInt256 length, int callDepth, out ReadOnlyMemory<byte> initCode)
    {
        if (!EvmPooledMemory.IsSwizzled) return memory.TryLoad(in location, in length, out initCode);

        initCode = LoadSwizzledInitCode(ref memory, in location, in length, callDepth);
        return true;
    }

    /// <summary>Reads the init code from swizzled memory into the code buffer of <paramref name="callDepth"/>, in natural byte order.</summary>
    /// <remarks>Out of line, as the creation handler it would otherwise be inlined into has an instance per fork.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReadOnlyMemory<byte> LoadSwizzledInitCode(ref EvmPooledMemory memory, in UInt256 location, in UInt256 length, int callDepth)
    {
        // Charging the expansion has checked the range.
        Span<byte> laidOut = memory.LoadLaidOutSpanAfterGas(in location, in length);
        if (laidOut.IsEmpty) return default;

        byte[] code = GetInitCodeBuffers(callDepth, laidOut.Length).Code!;
        SwizzledMemory.Read(ref MemoryMarshal.GetReference(laidOut), code.AsSpan(0, laidOut.Length));
        return code.AsMemory(0, laidOut.Length);
    }

    /// <summary>The buffers of the creation frames at <paramref name="callDepth"/>, grown to hold <paramref name="length"/> bytes of code.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref InitCodeBuffers GetInitCodeBuffers(int callDepth, int length)
    {
        ref InitCodeBuffers buffers = ref (_initCodeBuffers ??= new InitCodeBuffers[VirtualMachineStatics.MaxCallDepth + 1])[callDepth];
        byte[]? code = buffers.Code;
        if (code is null || code.Length - ExecutionPadding < length)
        {
            int capacity = length > 1 << 30 ? length : (int)BitOperations.RoundUpToPowerOf2((uint)length);
            buffers.Code = CreateExecutionBuffer(capacity);
            buffers.JumpBitmap = JumpDestinationAnalyzer.CreateBitmap(capacity);
        }

        return ref buffers;
    }

    private struct InitCodeBuffers
    {
        public byte[]? Code;
        public long[]? JumpBitmap;
    }

    public partial ReadOnlyMemory<byte> Code => _code;

    public partial ReadOnlySpan<byte> CodeSpan => _code.Span;

    internal partial int CodeLength => _code.Length;

    internal partial ReadOnlySpan<byte> ExecutionCodeSpan => _code.Span;

    /// <summary>The most memory all handler caches together take.</summary>
    /// <remarks>
    /// The guest never frees memory, and a block can run many large codes for little gas, such as creations with
    /// large init code, so the caches need a bound of their own. Real blocks take a few MiB.
    /// </remarks>
    private const long HandlerCacheBudget = 64L << 20;

    private static long s_handlerCacheBytes;
    private static nint[]? s_readOnlyHandlerCache;

    private nint[]? _handlerCache;
    private nint[]? _handlerCacheTable;

    /// <summary>
    /// The handlers guest dispatch has resolved for this code under the paired table <paramref name="table"/>, by
    /// position, or <see langword="null"/> once the caches have taken <see cref="HandlerCacheBudget"/>.
    /// </summary>
    /// <remarks>Another table, as a traced run uses, starts a new cache; one the budget refuses leaves the previous
    /// table's cache to the runs under that table.</remarks>
    internal nint[]? GetHandlerCache(nint[] table)
    {
        if (!ReferenceEquals(_handlerCacheTable, table))
        {
            nint[]? cache = TryCreateHandlerCache(CodeLength);
            if (cache is null) return null;
            _handlerCache = cache;
            _handlerCacheTable = table;
        }

        return _handlerCache;
    }

    /// <summary>An empty handler cache for <paramref name="codeLength"/> code bytes, or <see langword="null"/> past <see cref="HandlerCacheBudget"/>.</summary>
    internal static nint[]? TryCreateHandlerCache(int codeLength)
    {
        long bytes = (long)HandlerCacheLength(codeLength) * IntPtr.Size;
        if (s_handlerCacheBytes + bytes > HandlerCacheBudget) return null;
        s_handlerCacheBytes += bytes;
        return GC.AllocateArray<nint>(HandlerCacheLength(codeLength), pinned: true);
    }

    /// <summary>An empty handler cache for <paramref name="codeLength"/> code bytes that dispatch only reads, shared by every code past <see cref="HandlerCacheBudget"/>.</summary>
    internal static nint[] ReadOnlyHandlerCache(int codeLength)
    {
        nint[]? cache = s_readOnlyHandlerCache;
        if (cache is null || cache.Length < HandlerCacheLength(codeLength))
        {
            // The guest cannot reclaim previous arrays, so geometric growth bounds their total size too.
            int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)HandlerCacheLength(codeLength));
            s_readOnlyHandlerCache = cache = GC.AllocateArray<nint>(capacity, pinned: true);
        }
        return cache;
    }

    /// <summary>The slots a handler cache for <paramref name="codeLength"/> code bytes takes.</summary>
    /// <remarks>
    /// One slot for each position dispatch reaches: past a PUSH32 in the last byte, the next opcode is read at the
    /// next-to-last padding byte (see <see cref="ExecutionPadding"/>).
    /// </remarks>
    private static int HandlerCacheLength(int codeLength) => codeLength + ExecutionPadding - 1;

    // Guest execution is single-threaded; bitmap writes and the resume cursor are not synchronized.
    private long[]? _incrementalJumpBitmap;
    private nint _analyzedUntil;

    /// <summary>The jump-destination bitmap of this code, holding only the destinations analyzed so far.</summary>
    /// <remarks>
    /// Sized for the whole code so the shared bit test can index it, but a clear bit only means "not a
    /// destination, or not analyzed yet"; <see cref="AnalyzeJump"/> is what turns that into an answer.
    /// On ZisK the precompile analyzes the whole code when the bitmap is created, so there a clear bit is the answer.
    /// </remarks>
    internal long[] IncrementalJumpBitmap => _incrementalJumpBitmap ??= CreateJumpBitmap();

    private long[] CreateJumpBitmap()
    {
        long[]? bitmap = _jumpBitmapBuffer;
        if (bitmap is null)
        {
            bitmap = JumpDestinationAnalyzer.CreateBitmap(CodeLength);
        }
        else
        {
            // The buffer still holds the destinations of the init code that ran before at its call depth.
            Array.Clear(bitmap, 0, JumpDestinationAnalyzer.GetInt64ArrayLengthFromBitLength(CodeLength));
        }

        // STOP-first code halts before any jump, so it is not worth analyzing.
        if (ZiskFlag.IsActive && CodeLength != 0 && _code.Span[0] != (byte)Instruction.STOP) AnalyzeWithPrecompile(bitmap);
        return bitmap;
    }

    /// <summary>Marks every jump destination of this code in <paramref name="bitmap"/> with ZisK's JUMPDEST bitmap precompile.</summary>
    /// <remarks>
    /// The precompile takes two steps and a cost linear in the code length, which real code repays in lazy
    /// analysis: deferring it to the first destination the look-back cannot decide skips too few codes to pay
    /// for the look-backs. Moving the cursor to the end leaves every lazy path a plain bit test. It reads and
    /// writes whole aligned words, reading at most 7 bytes past the code, which the execution padding holds; an
    /// unaligned buffer keeps the lazy analysis, as the precompile cannot be proven on it.
    /// </remarks>
    private unsafe void AnalyzeWithPrecompile(long[] bitmap)
    {
        fixed (byte* code = _code.Span)
        fixed (long* bits = bitmap)
        {
            if ((((nuint)code | (nuint)bits) & (sizeof(long) - 1)) != 0) return;
            Accelerators.JumpDestBitmap((ulong*)bits, code, (nuint)CodeLength);
        }

        _analyzedUntil = CodeLength;
    }

    /// <summary>Extends the scan far enough to decide <paramref name="destination"/>, and reports whether it is a jump destination.</summary>
    /// <param name="destination">A destination inside the code.</param>
    /// <param name="bitmap">This code's <see cref="IncrementalJumpBitmap"/>.</param>
    /// <param name="code">This code.</param>
    /// <remarks>
    /// The guest pays for every byte it scans, so most destinations are proven by looking at the 32 bytes before
    /// them, and the rest are scanned from the nearest instruction start found that way rather than from the
    /// start of the code (see <see cref="JumpDestinationAnalyzer.AnalyzeJump"/>). The caller passes the bitmap
    /// and code it already holds.
    /// </remarks>
    // Out of line: it carries the whole inlined analysis, which the shared jump handlers reaching it should not.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal bool AnalyzeJump(int destination, long[] bitmap, ReadOnlySpan<byte> code) =>
        code[0] != (byte)Instruction.STOP && AnalyzeRunningJump(destination, bitmap, ref MemoryMarshal.GetReference(code));

    /// <summary>
    /// <see cref="AnalyzeJump"/> for <paramref name="destination"/>, a position inside the code whose bit is still
    /// clear, in code that does not start with STOP, if <paramref name="bitmap"/> is this code's <see cref="IncrementalJumpBitmap"/>.
    /// </summary>
    /// <param name="destination">A destination inside the code.</param>
    /// <param name="bitmap">The bitmap the jump is checked against.</param>
    /// <param name="code">The first byte of this code.</param>
    /// <remarks>
    /// Code that starts with STOP halts before any jump, so a running jump needs no test for it. The bitmap is compared
    /// with the field, as in <see cref="IsJumpProvenByLookBack"/>, so the scanned-jump handler makes no call.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool AnalyzeRunningJump(nint destination, long[]? bitmap, ref byte code) =>
        ReferenceEquals(bitmap, _incrementalJumpBitmap) &&
        Unsafe.Add(ref code, destination) == (byte)Instruction.JUMPDEST &&
        JumpDestinationAnalyzer.AnalyzeJump(destination, bitmap!, ref code, ref _analyzedUntil);

    /// <summary>
    /// Reports whether <paramref name="bitmap"/> is this code's <see cref="IncrementalJumpBitmap"/> and a single look-back
    /// proves <paramref name="destination"/> the destination of the jump running.
    /// </summary>
    /// <param name="destination">A destination inside the code.</param>
    /// <param name="bitmap">The bitmap the jump is checked against.</param>
    /// <param name="code">The first byte of this code.</param>
    /// <remarks>
    /// The part of <see cref="AnalyzeJump"/> that needs no call and no bounds check, so a frameless handler can take
    /// it; the caller marks a proven destination. A false answer leaves the destination to <see cref="AnalyzeJump"/>.
    /// Code that starts with STOP halts before any jump, so unlike <see cref="AnalyzeJump"/> this does not test for it.
    /// The bitmap is compared with the field rather than <see cref="IncrementalJumpBitmap"/>, whose lazy creation is a
    /// call that would give the handler a frame; a bitmap not created yet compares unequal and so is left to
    /// <see cref="AnalyzeJump"/> too.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsJumpProvenByLookBack(nint destination, long[]? bitmap, ref byte code) =>
        ReferenceEquals(bitmap, _incrementalJumpBitmap) &&
        Unsafe.Add(ref code, destination) == (byte)Instruction.JUMPDEST &&
        JumpDestinationAnalyzer.IsProvenByLookBack(destination, ref code, _analyzedUntil);
}
