// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

/// <summary>
/// A V8 runtime dedicated to one trace request, with the scripts it compiles cached for the request's engines.
/// </summary>
/// <remarks>
/// Each runtime is a V8 isolate with its own heap: a script that outgrows the soft limit interrupts only the trace
/// that runs it, traces served concurrently no longer serialise on one isolate lock, and disposing the runtime
/// with the trace releases everything the trace allocated, so no limit has to be re-armed for the next one.
/// Every limit below applies to one runtime, not to all the traces served at once.
/// The soft limit is a sampled, best-effort bound on the V8 heap: ClearScript samples the heap size whenever a hook
/// enters the script and periodically while it runs, and interrupts the script with its memory-limit error when the
/// heap is still over the limit after a full collection, so a script can allocate past it between two samples.
/// The old-space size caps V8's old generation; the total heap limit V8 reports is slightly higher because it also
/// covers the young generation. When V8 cannot satisfy an allocation within these constraints, it ends the process.
/// The old-space ceiling sits far above the soft limit to absorb what a script allocates between two samples.
/// A single call into a built-in that never reaches an interrupt check, such as <c>JSON.parse</c> of a large string,
/// is not sampled while it runs, so its V8 heap allocations are bounded only by this ceiling and by the memory
/// available to the process. The trace ends with the memory-limit error only if a later sample still finds the heap
/// over the soft limit after a collection. Array buffer memory is allocated outside the V8 heap, so neither limit
/// bounds it. Raising the ceiling does not preallocate that memory.
/// The size is fixed: ClearScript enforces the soft limit with last-resort collections, and V8 can raise its heap
/// limit through the near-heap-limit callback on those collections, so a heap expansion multiplier would raise the
/// ceiling each time a collection brings a script back under the soft limit, until it overflows and V8 aborts the
/// process.
/// A runtime serves the single thread that runs its trace and is not thread-safe.
/// </remarks>
internal sealed class TracerRuntime : IDisposable
{
    private const int MaxOldSpaceMb = 16384;
    private static readonly UIntPtr HeapSoftLimit = new(128 * 1024 * 1024);

    private const string TracersPath = "Data/JSTracers/";
    private const string Extension = ".js";
    private const string BigIntegerJavaScript = "_bigInteger.js";
    private const string CreateUint8ArrayCode = "(function (buffer) {return new Uint8Array(buffer);}).valueOf()";

    // PublicationOnly does not cache a failed load, so a later request retries it instead of rethrowing it for the process lifetime.
    private static readonly Lazy<Dictionary<string, string>> _builtInSources = new(LoadBuiltInSources, LazyThreadSafetyMode.PublicationOnly);

    private readonly V8Runtime _runtime;
    private readonly Dictionary<string, V8Script> _scripts = [];

    public TracerRuntime()
    {
        _runtime = new V8Runtime(new V8RuntimeConstraints
        {
            MaxOldSpaceSize = MaxOldSpaceMb
        });

        try
        {
            _runtime.MaxHeapSize = HeapSoftLimit;
        }
        catch
        {
            _runtime.Dispose();
            throw;
        }
    }

    public V8ScriptEngine CreateScriptEngine(V8ScriptEngineFlags flags) => _runtime.CreateScriptEngine(flags);

    /// <summary>
    /// V8's hard limit on this runtime's heap in bytes, the old-space size plus the young generation.
    /// </summary>
    internal ulong HeapSizeLimit => _runtime.GetHeapInfo().HeapSizeLimit;

    public V8Script BigInteger => GetScript(BigIntegerJavaScript,
        _builtInSources.Value.TryGetValue(BigIntegerJavaScript, out string? code)
            ? code
            : throw new FileNotFoundException($"JavaScript tracer helper '{BigIntegerJavaScript}' is missing from {TracersPath}"));

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
            return GetScript(tracer, tracer, pack: true);
        }

        string fileName = ToTracerFileName(tracer);
        return IsBuiltIn(fileName)
            ? GetScript(fileName, _builtInSources.Value[fileName], pack: true)
            : throw new ArgumentException($"Tracer '{tracer}' not found");
    }

    /// <summary>
    /// Reports whether <paramref name="tracer"/> is inline tracer code or names a tracer shipped under
    /// <c>Data/JSTracers</c>, so a request naming anything else can be refused before a runtime is created.
    /// </summary>
    private static bool IsKnownTracer(string? tracer)
    {
        if (tracer is null)
        {
            return false;
        }

        tracer = tracer.Trim();
        return IsInline(tracer) || IsBuiltIn(ToTracerFileName(tracer));
    }

    /// <summary>
    /// Refuses a tracer no engine could load, returning the runtime inline code was compiled in so the tracer can
    /// reuse it and its cached script, or <see langword="null"/> for a shipped tracer, which needs no compile check.
    /// </summary>
    /// <exception cref="ArgumentException">The tracer is not found or its code does not compile.</exception>
    public static TracerRuntime? CreateValidated(string? tracer)
    {
        if (!IsKnownTracer(tracer))
        {
            throw new ArgumentException($"Tracer '{tracer}' not found");
        }

        if (!IsInline(tracer!.Trim()))
        {
            return null;
        }

        TracerRuntime runtime = new();
        try
        {
            runtime.GetTracerScript(tracer);
            return runtime;
        }
        catch (ScriptEngineException e) when (!e.IsFatal)
        {
            runtime.Dispose();
            throw new ArgumentException($"Tracer code could not be compiled: {e.Message}", e);
        }
        catch
        {
            runtime.Dispose();
            throw;
        }
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

    private V8Script GetScript(string key, string code, bool pack = false)
    {
        if (!_scripts.TryGetValue(key, out V8Script? script))
        {
            script = _runtime.Compile(pack ? Pack(code) : code);
            _scripts[key] = script;
        }

        return script;
    }

    private static bool IsInline(string tracer) => tracer.StartsWith('{') && tracer.EndsWith('}');

    // Names starting with '_' are the engine's own scripts, not tracers.
    private static bool IsBuiltIn(string fileName) => !fileName.StartsWith('_') && _builtInSources.Value.ContainsKey(fileName);

    private static string ToTracerFileName(string tracer) =>
        Path.GetExtension(tracer).Equals(Extension, StringComparison.OrdinalIgnoreCase) ? Path.ChangeExtension(tracer, Extension) : tracer + Extension;

    private static string Pack(string tracerObjectCode) => "(" + tracerObjectCode + ")";

    private static Dictionary<string, string> LoadBuiltInSources()
    {
        Dictionary<string, string> sources = [];
        foreach (string file in Directory.EnumerateFiles(TracersPath.GetApplicationResourcePath(), $"*{Extension}", SearchOption.AllDirectories))
        {
            sources[Path.GetFileName(file)] = File.ReadAllText(file);
        }

        return sources;
    }
}
