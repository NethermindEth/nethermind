// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.ClearScript.V8;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

/// <summary>
/// A V8 runtime dedicated to one block trace, with the scripts it compiles cached for the block's engines.
/// </summary>
/// <remarks>
/// Each runtime is a V8 isolate with its own heap: a script that outgrows the soft limit interrupts only the trace
/// that runs it, traces served concurrently no longer serialise on one isolate lock, and disposing the runtime
/// with the trace releases everything the trace allocated, so no limit has to be re-armed for the next one.
/// The old-space size above the soft limit is the backstop for the allocation burst between two heap samples.
/// A runtime serves the single thread that traces its block and is not thread-safe.
/// </remarks>
internal sealed class TracerRuntime : IDisposable
{
    private const int MaxOldSpaceMb = 256;
    private static readonly UIntPtr HeapSoftLimit = new(128 * 1024 * 1024);

    private const string TracersPath = "Data/JSTracers/";
    private const string Extension = "js";
    private const string BigIntegerJavaScript = "_bigInteger.js";
    private const string CreateUint8ArrayCode = "(function (buffer) {return new Uint8Array(buffer);}).valueOf()";

    private static readonly Lazy<Dictionary<string, string>> _builtInSources = new(LoadBuiltInSources);

    private readonly V8Runtime _runtime = new(new V8RuntimeConstraints { MaxOldSpaceSize = MaxOldSpaceMb }) { MaxHeapSize = HeapSoftLimit };
    private readonly Dictionary<string, V8Script> _scripts = [];

    public V8ScriptEngine CreateScriptEngine(V8ScriptEngineFlags flags) => _runtime.CreateScriptEngine(flags);

    public V8Script BigInteger => GetScript(BigIntegerJavaScript, _builtInSources.Value[BigIntegerJavaScript]);

    public V8Script CreateUint8Array => GetScript(nameof(CreateUint8ArrayCode), CreateUint8ArrayCode);

    /// <summary>
    /// Compiles the tracer given as inline code or as the name of a tracer shipped under <c>Data/JSTracers</c>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="tracer"/> is not a known tracer, see <see cref="IsKnownTracer"/>.</exception>
    public V8Script GetTracerScript(string tracer)
    {
        tracer = tracer.Trim();
        if (IsInline(tracer))
        {
            return GetScript(tracer, Pack(tracer));
        }

        string fileName = ToTracerFileName(tracer);
        return IsBuiltIn(fileName)
            ? GetScript(fileName, Pack(_builtInSources.Value[fileName]))
            : throw new ArgumentException($"Tracer '{tracer}' not found");
    }

    /// <summary>
    /// Reports whether <paramref name="tracer"/> is inline tracer code or names a tracer shipped under
    /// <c>Data/JSTracers</c>, so a request naming anything else can be refused before a runtime is created.
    /// </summary>
    public static bool IsKnownTracer(string tracer)
    {
        tracer = tracer.Trim();
        return IsInline(tracer) || IsBuiltIn(ToTracerFileName(tracer));
    }

    public void Dispose()
    {
        foreach (V8Script script in _scripts.Values)
        {
            script.Dispose();
        }

        _scripts.Clear();
        _runtime.Dispose();
    }

    private V8Script GetScript(string key, string code)
    {
        if (!_scripts.TryGetValue(key, out V8Script? script))
        {
            script = _runtime.Compile(code);
            _scripts[key] = script;
        }

        return script;
    }

    private static bool IsInline(string tracer) => tracer.StartsWith('{') && tracer.EndsWith('}');

    // Names starting with '_' are the engine's own scripts, not tracers.
    private static bool IsBuiltIn(string fileName) => !fileName.StartsWith('_') && _builtInSources.Value.ContainsKey(fileName);

    private static string ToTracerFileName(string tracer) =>
        !Path.HasExtension(tracer) || Path.GetExtension(tracer) != Extension ? Path.ChangeExtension(tracer, Extension) : tracer;

    private static string Pack(string tracerObjectCode) => "(" + tracerObjectCode + ")";

    private static Dictionary<string, string> LoadBuiltInSources()
    {
        Dictionary<string, string> sources = [];
        foreach (string file in Directory.EnumerateFiles(TracersPath.GetApplicationResourcePath(), $"*.{Extension}", SearchOption.AllDirectories))
        {
            sources[Path.GetFileName(file)] = File.ReadAllText(file);
        }

        return sources;
    }
}
