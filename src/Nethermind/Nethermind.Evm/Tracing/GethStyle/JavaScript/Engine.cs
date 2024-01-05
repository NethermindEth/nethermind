// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
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
using Nethermind.Evm.Precompiles;
using Nethermind.Logging;
#pragma warning disable CS0162 // Unreachable code detected

namespace Nethermind.Evm.Tracing.GethStyle.JavaScript;

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

    [ThreadStatic] private static Engine? _currentEngine;

    private static readonly V8Runtime _runtime = new();
    private static readonly ConcurrentDictionary<string, V8Script> _builtInScripts = new();
    private static readonly LruCache<int, V8Script> _runtimeScripts = new(10, "runtime scripts");

    public static Engine? CurrentEngine
    {
        get => _currentEngine;
        set => _currentEngine = value;
    }

    static Engine()
    {
        // compile default scripts in background thread
        Task.Run(CompileStandardScripts);
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
        foreach ((string Name, string Code) tracer in LoadJavaScriptCodeFromFiles())
        {
            LoadBuiltIn(tracer.Name, tracer.Code);
        }
    }

    private static V8Script LoadBuiltIn(string name, string code) => _builtInScripts.AddOrUpdate(name, c => _runtime.Compile(code), static (_, script) => script);

    public Engine(IReleaseSpec spec)
    {
        _spec = spec;

        V8Engine = _runtime.CreateScriptEngine(IsDebugging
            ? V8ScriptEngineFlags.AwaitDebuggerAndPauseOnStart | V8ScriptEngineFlags.EnableDebugging
            : V8ScriptEngineFlags.None);

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

        Interlocked.CompareExchange(ref _currentEngine, this, null);
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
    private ITypedArray<byte> ToAddress(object address) => address.ToAddress().Bytes.ToTypedScriptArray();

    /// <summary>
    /// Checks if contract at given address is a precompile
    /// </summary>
    private bool IsPrecompiled(object address) => address.ToAddress().IsPrecompile(_spec);

    /// <summary>
    /// Returns a slice of input
    /// </summary>
    private ITypedArray<byte> Slice(object input, long start, long end)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bytes = input.ToBytes();

        return start < 0 || end < start || end > bytes.Length
            ? throw new ArgumentOutOfRangeException(nameof(start), $"tracer accessed out of bound memory: available {bytes.Length}, offset {start}, size {end - start}")
            : bytes.Slice((int)start, (int)(end - start)).ToTypedScriptArray();
    }

    /// <summary>
    /// Creates a contract address from sender and nonce (used for CREATE instruction)
    /// </summary>
    private ITypedArray<byte> ToContract(object from, ulong nonce) => ContractAddress.From(from.ToAddress(), nonce).Bytes.ToTypedScriptArray();

    /// <summary>
    /// Creates a contract address from sender, salt and initcode (used for CREATE2 instruction)
    /// </summary>
    private ITypedArray<byte> ToContract2(object from, string salt, object initcode) =>
        ContractAddress.From(from.ToAddress(), Bytes.FromHexString(salt, EvmStack.WordSize), initcode.ToBytes()).Bytes.ToTypedScriptArray();

    public void Dispose()
    {
        Interlocked.CompareExchange(ref _currentEngine, null, this);
        V8Engine.Dispose();
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
                int hashCode = tracer.GetHashCode();
                if (_runtimeScripts.TryGet(hashCode, out V8Script script))
                {
                    return script;
                }

                script = _runtime.Compile(PackTracerCode(tracer));
                if (_runtimeScripts.Set(hashCode, script))
                {
                    return script;
                }

                script.Dispose();
                return _runtimeScripts.Get(hashCode);
            }
            else
            {
                if (!Path.HasExtension(tracer) || Path.GetExtension(tracer) != Extension)
                {
                    tracer = Path.ChangeExtension(tracer, Extension);
                }

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
                if (!Path.HasExtension(tracer) || Path.GetExtension(tracer) != Extension)
                {
                    tracer = Path.ChangeExtension(tracer, Extension);
                }

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

    private static string LoadJavaScriptCodeFromFile(string tracerFileName) =>
        File.ReadAllText(Path.Combine(TracersPath, tracerFileName).GetApplicationResourcePath());

    private static string LoadTracerCodeFromFile(string tracerFileName) => PackTracerCode(LoadJavaScriptCodeFromFile(tracerFileName));

    private static V8Script LoadBigInteger() => LoadBuiltIn(nameof(BigIntegerJavaScript), LoadJavaScriptCodeFromFile(BigIntegerJavaScript));
}
