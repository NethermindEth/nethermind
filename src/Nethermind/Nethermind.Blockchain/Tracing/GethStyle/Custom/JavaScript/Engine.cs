// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;
using Nethermind.Core.Caching;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;
#pragma warning disable CS0162 // Unreachable code detected

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

public class Engine : IDisposable
{
    private const bool IsDebugging = false;
    private V8ScriptEngine V8Engine { get; }

    private const string BigIntegerJavaScript = "_bigInteger.js";

    private const string CreateUint8ArrayCode = "(function (buffer) {return new Uint8Array(buffer);}).valueOf()";
    private const string TracersPath = "Data/JSTracers/";
    private const string Extension = "js";

    private readonly IReleaseSpec _spec;

    private dynamic _bigInteger;
    private dynamic _createUint8Array;
    private int _disposed;

    [ThreadStatic] private static Engine? _currentEngine;

    private const int V8MaxOldSpaceMb = 256;
    private const double V8HeapExpansionMultiplier = 2;
    private static readonly UIntPtr V8HeapSoftLimit = new(128 * 1024 * 1024);

    private static readonly V8Runtime _runtime = CreateRuntime();
    private static int _liveEngines;
    private static readonly ConcurrentDictionary<string, V8Script> _builtInScripts = new();
    private static readonly LruCache<string, V8Script> _runtimeScripts = new(10, "runtime scripts");

    public static Engine? CurrentEngine
    {
        get => _currentEngine;
        set => _currentEngine = value;
    }

    static Engine() =>
        // compile default scripts in background thread
        Task.Run(CompileStandardScripts);

    /// <summary>
    /// Creates the runtime shared by every engine in the process. The monitored soft limit is the effective bound
    /// on the script heap: it interrupts a script that outgrows it. The old-space size sits above it and the
    /// expansion multiplier absorbs the allocation burst between two heap samples, so together they are the
    /// backstop that keeps the sampler ahead of the runtime's own hard limit.
    /// </summary>
    private static V8Runtime CreateRuntime()
    {
        V8Runtime runtime = new(new V8RuntimeConstraints
        {
            MaxOldSpaceSize = V8MaxOldSpaceMb,
            HeapExpansionMultiplier = V8HeapExpansionMultiplier
        });
        runtime.MaxHeapSize = V8HeapSoftLimit;
        return runtime;
    }

    /// <summary>
    /// A soft-limit violation blocks every script in the runtime until the limit is set again. The limit is
    /// re-armed only when the live-engine count leaves or returns to zero, so releasing one engine cannot lift a
    /// violation raised against a script that is still alive in another. Zero is always reached: while the
    /// violation stands no engine can complete a script call, so every live engine fails and is released, and a
    /// construction that fails releases its count as well. The count must stay balanced: an engine that is never
    /// released disables recovery for the process, so engines belong to the tracer lifecycle only.
    /// </summary>
    private static void RearmHeapSoftLimit() => _runtime.MaxHeapSize = V8HeapSoftLimit;

    private static void AcquireLiveEngine()
    {
        if (Interlocked.Increment(ref _liveEngines) != 1)
        {
            return;
        }

        try
        {
            RearmHeapSoftLimit();
        }
        catch
        {
            Interlocked.Decrement(ref _liveEngines);
            throw;
        }
    }

    private static void ReleaseLiveEngine()
    {
        if (Interlocked.Decrement(ref _liveEngines) == 0)
        {
            RearmHeapSoftLimit();
        }
    }

    private static string PackTracerCode(string tracerObjectCode) => "(" + tracerObjectCode + ")";

    private static void CompileStandardScripts()
    {
        static IEnumerable<(string Name, string Code)> LoadJavaScriptCodeFromFiles()
        {
            foreach (string tracer in Directory.EnumerateFiles(TracersPath.GetApplicationResourcePath(), $"*.{Extension}", SearchOption.AllDirectories))
            {
                yield return (Path.GetFileName(tracer), PackTracerCode(File.ReadAllText(tracer)));
            }
        }

        LoadBigInteger();
        LoadBuiltIn(nameof(CreateUint8ArrayCode), CreateUint8ArrayCode);
        foreach ((string Name, string Code) in LoadJavaScriptCodeFromFiles())
        {
            LoadBuiltIn(Name, Code);
        }
    }

    private static V8Script LoadBuiltIn(string name, string code) => _builtInScripts.AddOrUpdate(name, c => _runtime.Compile(code), static (_, script) => script);

    public Engine(IReleaseSpec spec)
    {
        _spec = spec;

        AcquireLiveEngine();
        V8ScriptEngine? scriptEngine = null;
        try
        {
            scriptEngine = _runtime.CreateScriptEngine(IsDebugging
                ? V8ScriptEngineFlags.AwaitDebuggerAndPauseOnStart | V8ScriptEngineFlags.EnableDebugging
                : V8ScriptEngineFlags.None);
            V8Engine = scriptEngine;
            Initialize();
        }
        catch
        {
            // Creating the script engine and initializing it both run script, which fails while a heap-limit
            // violation is pending. Release the script engine and the live count so nothing leaks and the
            // violation cannot outlive the last engine.
            try
            {
                scriptEngine?.Dispose();
            }
            finally
            {
                ReleaseLiveEngine();
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

        if (!IsDebugging)
        {
            _bigInteger = V8Engine.Evaluate(LoadBigInteger());
            _createUint8Array = V8Engine.Evaluate(LoadBuiltIn(nameof(CreateUint8ArrayCode), CreateUint8ArrayCode));
        }
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
            V8Engine.Dispose();
        }
        finally
        {
            ReleaseLiveEngine();
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
    public dynamic CreateTracer(string tracer)
    {
        static V8Script LoadJavaScriptCode(string tracer)
        {
            tracer = tracer.Trim();
            if (tracer.StartsWith('_'))
            {
                throw new ArgumentException($"Cannot access internal tracer '{tracer}'");
            }
            else if (tracer.StartsWith('{') && tracer.EndsWith('}'))
            {
                return _runtimeScripts.SetOrGet(
                    tracer,
                    tracer,
                    static (_, tracerCode) => _runtime.Compile(PackTracerCode(tracerCode)));
            }
            else
            {
                tracer = ToTracerFileName(tracer);

                return _builtInScripts.TryGetValue(tracer, out V8Script script)
                    ? script
                    // fallback, shouldn't happen if the tracers were initialized from file before
                    : LoadBuiltIn(tracer, LoadTracerCodeFromFile(tracer));
            }
        }

        static string LoadJavaScriptDebugCode(string tracer)
        {
            tracer = tracer.Trim();
            if (tracer.StartsWith('{') && tracer.EndsWith('}'))
            {
                return PackTracerCode(tracer);
            }
            else
            {
                tracer = ToTracerFileName(tracer);

                return LoadTracerCodeFromFile(tracer);
            }
        }

        if (IsDebugging)
        {
            object tracerObj = V8Engine.Evaluate(LoadJavaScriptDebugCode(tracer));
            _bigInteger = V8Engine.Evaluate(LoadBigInteger());
            _createUint8Array = V8Engine.Evaluate(LoadBuiltIn(nameof(CreateUint8ArrayCode), CreateUint8ArrayCode));
            return tracerObj;
        }
        else
        {
            return V8Engine.Evaluate(LoadJavaScriptCode(tracer));
        }
    }

    /// <summary>
    /// Reports whether <paramref name="tracer"/> is inline tracer code or names a tracer shipped under
    /// <c>Data/JSTracers</c>, so a request naming anything else can be refused before a script engine is created.
    /// </summary>
    public static bool IsKnownTracer(string tracer)
    {
        tracer = tracer.Trim();
        if (tracer.StartsWith('_'))
        {
            return false;
        }

        if (tracer.StartsWith('{') && tracer.EndsWith('}'))
        {
            return true;
        }

        string fileName = ToTracerFileName(tracer);
        return Path.GetFileName(fileName) == fileName
            && (_builtInScripts.ContainsKey(fileName) || File.Exists(Path.Combine(TracersPath, fileName).GetApplicationResourcePath()));
    }

    private static string ToTracerFileName(string tracer) =>
        !Path.HasExtension(tracer) || Path.GetExtension(tracer) != Extension ? Path.ChangeExtension(tracer, Extension) : tracer;

    private static string LoadJavaScriptCodeFromFile(string tracerFileName) =>
        File.ReadAllText(Path.Combine(TracersPath, tracerFileName).GetApplicationResourcePath());

    private static string LoadTracerCodeFromFile(string tracerFileName) => PackTracerCode(LoadJavaScriptCodeFromFile(tracerFileName));

    private static V8Script LoadBigInteger() => _builtInScripts.TryGetValue(nameof(BigIntegerJavaScript), out V8Script script)
        ? script
        : LoadBuiltIn(nameof(BigIntegerJavaScript), LoadJavaScriptCodeFromFile(BigIntegerJavaScript));
}
