using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace RpcReplay;

/// <summary>
/// Records where the JIT put each method it compiled - start, size, tier and name - for an out-of-process sampler to
/// attribute instruction pointers to, when <c>RPCPOC_CODEMAP</c> names the file to write.
/// </summary>
internal sealed class CodeMapListener : EventListener
{
    private readonly ConcurrentQueue<string> _lines = new();

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)(0x10 | 0x20000)); // JitKeyword: MethodLoadVerbose; JittedMethodILToNativeMap
    }

    private static object Field(EventWrittenEventArgs e, string name)
    {
        ReadOnlyCollection<string> names = e.PayloadNames;
        int index = names?.IndexOf(name) ?? -1;
        return index >= 0 ? e.Payload[index] : null;
    }

    private readonly ConcurrentDictionary<ulong, string> _maps = new();

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is not null && e.EventName.StartsWith("MethodILToNativeMap", StringComparison.Ordinal))
        {
            // IL offset to native offset pairs, for the compiled chunks only: matched to their loads by method id.
            if (Field(e, "ILOffsets") is Array il && Field(e, "NativeOffsets") is Array native)
            {
                System.Text.StringBuilder pairs = new();
                for (int i = 0; i < Math.Min(il.Length, native.Length); i++)
                    pairs.Append(Convert.ToUInt32(il.GetValue(i))).Append(':').Append(Convert.ToUInt32(native.GetValue(i))).Append(' ');
                _maps[Convert.ToUInt64(Field(e, "MethodID"))] = pairs.ToString();
            }

            return;
        }

        if (e.EventName is null || !e.EventName.StartsWith("MethodLoadVerbose", StringComparison.Ordinal)) return;
        ulong start = Convert.ToUInt64(Field(e, "MethodStartAddress"));
        uint size = Convert.ToUInt32(Field(e, "MethodSize"));
        uint flags = Convert.ToUInt32(Field(e, "MethodFlags"));
        ulong id = Convert.ToUInt64(Field(e, "MethodID"));
        string name = $"{Field(e, "MethodNamespace")}::{Field(e, "MethodName")}";
        if ((flags & 1) == 0 && (flags & 2) != 0)
        {
            // A generic instantiation: its type arguments tell the helpers' operations apart.
            try
            {
                MethodBase method = MethodBase.GetMethodFromHandle(RuntimeMethodHandle.FromIntPtr((nint)id));
                if (method is not null)
                    name = $"{method.DeclaringType?.Name}::{method.Name}[{string.Join(",", method.GetGenericArguments().Select(static a => a.Name))}]";
            }
            catch
            {
            }
        }

        int tier = (int)((flags >> 7) & 0x7);
        _lines.Enqueue($"{start:x} {size:x} {tier} {flags:x} {name.Replace(' ', '_')}");
        if ((flags & 1) != 0 && name.Contains("evm_", StringComparison.Ordinal))
        {
            _dynamic.Enqueue((id, start, name));
            Interlocked.Increment(ref _chunks);
            Interlocked.Add(ref _chunkBytes, size);
        }
    }

    private readonly ConcurrentQueue<(ulong Id, ulong Start, string Name)> _dynamic = new();
    private long _chunkBytes, _chunks;

    /// <summary>The compiled chunks the JIT loaded so far: how many, and their native code in all.</summary>
    public string DescribeChunks() => $"evm chunks: {Interlocked.Read(ref _chunks)} methods, {Interlocked.Read(ref _chunkBytes)} bytes of native code";

    public void Write(string path)
    {
        File.WriteAllLines(path, _lines.ToArray());
        Console.WriteLine($"code map: {_lines.Count} methods written to {path}");
        // The chunks' IL-to-native maps, and where in their IL the plugin's emitter started each opcode (NETHERMIND_ILEVM_ILMAP=1).
        using StreamWriter maps = new(path + ".il");
        int written = 0;
        foreach ((ulong id, ulong start, string name) in _dynamic)
        {
            if (!_maps.TryGetValue(id, out string pairs)) continue;
            maps.WriteLine($"native {start:x} {name} {pairs}");
            written++;
        }

        foreach ((int id, (string Method, string Category, int Pc) marker) in Nethermind.IlEvm.IlEvmDiagnostics.MarkerNames)
            maps.WriteLine($"marker {id}\t{marker.Method}\t{marker.Category}\t{marker.Pc}");
        Console.WriteLine($"code map: {written} native maps, {Nethermind.IlEvm.IlEvmDiagnostics.MarkerNames.Count} markers written to {path}.il");
    }
}
