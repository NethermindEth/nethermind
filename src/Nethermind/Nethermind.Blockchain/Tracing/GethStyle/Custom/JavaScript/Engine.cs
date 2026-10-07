// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Threading;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
#pragma warning disable CS0162 // Unreachable code detected

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

public class Engine : IDisposable
{
    private const bool IsDebugging = false;
    private V8ScriptEngine V8Engine { get; }

    private readonly IReleaseSpec _spec;
    private readonly TracerRuntime _runtime;
    private readonly bool _ownsRuntime;

    private ScriptObject? _nullThrowInvoker;
    private NullThrowObserver? _availableNullThrowObserver;
    private dynamic _bigInteger;
    private dynamic _createUint8Array;
    private int _disposed;
    private int _externalInterruptRequested;
    private JavaScriptInputException? _inputError;

    [ThreadStatic] private static Engine? _currentEngine;

    public static Engine? CurrentEngine
    {
        get => _currentEngine;
        set => _currentEngine = value;
    }

    /// <summary>
    /// Creates an engine in a runtime of its own, released together with the engine.
    /// </summary>
    public Engine(IReleaseSpec spec) : this(spec, new TracerRuntime(), ownsRuntime: true)
    {
    }

    /// <summary>
    /// Creates an engine in its block tracer's runtime, which outlives the engine and is released by its owner.
    /// </summary>
    internal Engine(IReleaseSpec spec, TracerRuntime runtime) : this(spec, runtime, ownsRuntime: false)
    {
    }

    private Engine(IReleaseSpec spec, TracerRuntime runtime, bool ownsRuntime)
    {
        _spec = spec;
        _runtime = runtime;
        _ownsRuntime = ownsRuntime;

        V8ScriptEngine? scriptEngine = null;
        try
        {
            scriptEngine = runtime.CreateScriptEngine(IsDebugging
                ? V8ScriptEngineFlags.AwaitDebuggerAndPauseOnStart | V8ScriptEngineFlags.EnableDebugging
                : V8ScriptEngineFlags.None);
            V8Engine = scriptEngine;
            Initialize();
        }
        catch
        {
            // Creating the script engine and initializing it both run script, which fails while a heap-limit
            // violation is pending in the runtime. Release what was created so nothing leaks.
            try
            {
                scriptEngine?.Dispose();
            }
            finally
            {
                if (ownsRuntime)
                {
                    runtime.Dispose();
                }
            }

            throw;
        }

        Interlocked.CompareExchange(ref _currentEngine, this, null);
    }

    /// <summary>
    /// Registers the host functions and evaluates the built-in scripts into the script engine.
    /// </summary>
    private void Initialize()
    {
        Func<object, ITypedArray<byte>> toWord = ToWord;
        Func<object?, string> toHex = ToHex;
        Func<object, ITypedArray<byte>> toAddress = ToAddress;
        Func<object, bool> isPrecompiled = IsPrecompiled;
        Func<object, long, long, ITypedArray<byte>> slice = Slice;
        Func<object, ulong, ITypedArray<byte>> toContract = ToContract;
        Func<object, string, object, ITypedArray<byte>> toContract2 = ToContract2;

        V8Engine.AddHostObject(nameof(toWord), toWord);
        V8Engine.AddHostObject(nameof(toHex), toHex);
        V8Engine.AddHostObject(nameof(toAddress), toAddress);
        V8Engine.AddHostObject(nameof(isPrecompiled), isPrecompiled);
        V8Engine.AddHostObject(nameof(slice), slice);
        V8Engine.AddHostObject(nameof(toContract), toContract);
        V8Engine.AddHostObject(nameof(toContract2), toContract2);

        _bigInteger = V8Engine.Evaluate(_runtime.BigInteger);
        _createUint8Array = V8Engine.Evaluate(_runtime.CreateUint8Array);
    }

    /// <summary>
    /// Converts input to 32 byte word
    /// </summary>
    private ITypedArray<byte> ToWord(object bytes) => bytes.ToWord().ToTypedScriptArray();

    /// <summary>
    /// Converts input to hex string
    /// </summary>
    private string ToHex(object? bytes) => bytes is null ? "0x" : bytes.ToBytes().ToHexString();

    /// <summary>
    /// Converts input to 20 byte Address byte representation
    /// </summary>
    private ITypedArray<byte> ToAddress(object address) => address.ToAddress().Bytes.ToArray().ToTypedScriptArray();

    /// <summary>
    /// Checks if contract at given address is a precompile
    /// </summary>
    private bool IsPrecompiled(object address) => _spec.IsPrecompile(address.ToAddress());

    /// <summary>
    /// Returns a slice of input
    /// </summary>
    private ITypedArray<byte> Slice(object input, long start, long end)
    {
        ArgumentNullException.ThrowIfNull(input);
        byte[] bytes = input.ToBytes();

        return start < 0 || end < start || end > bytes.Length
            ? throw new ArgumentOutOfRangeException(nameof(start), $"tracer accessed out of bound memory: available {bytes.Length}, offset {start}, size {end - start}")
            : bytes.Slice((int)start, (int)(end - start)).ToTypedScriptArray();
    }

    /// <summary>
    /// Creates a contract address from sender and nonce (used for CREATE instruction)
    /// </summary>
    private ITypedArray<byte> ToContract(object from, ulong nonce) => ContractAddress.From(from.ToAddress(), nonce).Bytes.ToArray().ToTypedScriptArray();

    /// <summary>
    /// Creates a contract address from sender, salt and initcode (used for CREATE2 instruction)
    /// </summary>
    private ITypedArray<byte> ToContract2(object from, string salt, object initcode) =>
        ContractAddress.From(from.ToAddress(), Bytes.FromHexString(salt, EvmStack.WordSize), initcode.ToBytes()).Bytes.ToArray().ToTypedScriptArray();

    /// <summary>
    /// Stops the running script. Called from a timer thread, so the engine may be disposed between the check
    /// and the call.
    /// </summary>
    public void Interrupt()
    {
        Interlocked.Exchange(ref _externalInterruptRequested, 1);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            V8Engine.Interrupt();
        }
        catch (ObjectDisposedException)
        {
            // The timeout fired after the tracing thread released the engine: there is no script left to stop,
            // and the timer thread has no caller to report to.
        }
    }

    /// <summary>
    /// Releases the engine. Must run on the thread that created it: <see cref="CurrentEngine"/> is thread-local,
    /// and disposing elsewhere would leave the creating thread pointing at a disposed engine until its block
    /// trace ends.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.CompareExchange(ref _currentEngine, null, this);
        try
        {
            try
            {
                _nullThrowInvoker?.Dispose();
            }
            finally
            {
                V8Engine.Dispose();
            }
        }
        finally
        {
            if (_ownsRuntime)
            {
                _runtime.Dispose();
            }
        }
    }

    /// <summary>
    /// Creates a JavaScript V8Engine typed byte array
    /// </summary>
    public ITypedArray<byte> CreateUint8Array(byte[] buffer) => _createUint8Array(buffer);

    /// <summary>
    /// Creates a JavaScript BigInteger object
    /// </summary>
    public IJavaScriptObject CreateBigInteger(BigInteger value) => _bigInteger(value);

    /// <summary>
    /// Creates a JavaScript tracer object from JavaScript code or name
    /// </summary>
    public dynamic CreateTracer(string tracer) => V8Engine.Evaluate(_runtime.GetTracerScript(tracer));

    // Capture the intrinsic before user code runs; strict frames hide the per-call marker from caller introspection.
    internal void PrepareNullThrowCapture() => _nullThrowInvoker ??= (ScriptObject)V8Engine.Evaluate("""
        (function () {
            'use strict';
            const apply = Reflect.apply;
            return function (receiver, callback, markNull, first, second, hasSecond) {
                try {
                    return apply(callback, receiver, hasSecond ? [first, second] : [first]);
                } catch (error) {
                    if (error === null) markNull();
                    throw error;
                }
            };
        })()
        """);

    internal object? InvokeCapturingNull(object receiver, ScriptObject callback, object? first, object? second, bool hasSecond, out bool observedNullThrow)
    {
        observedNullThrow = false;
        NullThrowObserver observer = Interlocked.Exchange(ref _availableNullThrowObserver, null) ?? new();
        try
        {
            return _nullThrowInvoker!.Invoke(false, receiver, callback, observer.MarkNull, first, second, hasSecond);
        }
        catch
        {
            // Publish before rethrow: the caller's exception filter runs before this frame's finally blocks.
            observedNullThrow = observer.CaughtNull;
            throw;
        }
        finally
        {
            observer.CaughtNull = false;
            Interlocked.CompareExchange(ref _availableNullThrowObserver, observer, null);
        }
    }

    /// <remarks>
    /// Callers must return without accessing the invalid input; V8 may deliver the interruption at a later safepoint.
    /// </remarks>
    internal JavaScriptInputException AbortInput(string message)
    {
        _inputError ??= new JavaScriptInputException(message);
        V8Engine.Interrupt();
        return _inputError;
    }

    internal void ThrowIfInputFailed()
    {
        ThrowIfInterrupted();
        if (_inputError is { } error) throw error;
    }

    internal void ThrowIfInterrupted()
    {
        if (Volatile.Read(ref _externalInterruptRequested) != 0)
            throw new ScriptInterruptedException("Script execution interrupted");
    }

    internal bool TryGetInputError(Exception exception, out string? message, bool observedNullThrow = false)
    {
        message = null;
        // V8 uses the same interruption exception for input aborts and external requests; external requests take precedence.
        if (_inputError is null || Volatile.Read(ref _externalInterruptRequested) != 0
            || (!JavaScriptTraceFailure.IsInputFailure(exception, _inputError)
                && !JavaScriptTraceFailure.IsRecoverable(exception, observedNullThrow))) return false;
        message = _inputError.Message;
        return true;
    }

    private sealed class NullThrowObserver
    {
        public bool CaughtNull;
        public Action MarkNull { get; }

        public NullThrowObserver() => MarkNull = () => CaughtNull = true;
    }
}
