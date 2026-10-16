using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Tracing;
using System.Linq;

namespace RpcReplay;

/// <summary>Collects the JIT's inlining decisions inside compiled EVM chunks, when <c>RPCPOC_JITTRACE=1</c>.</summary>
internal sealed class JitInlineListener : EventListener
{
    private readonly ConcurrentDictionary<string, int> _failures = new();
    private readonly ConcurrentDictionary<string, int> _successes = new();
    private readonly ConcurrentDictionary<string, int> _failuresByMethod = new();
    private readonly ConcurrentDictionary<string, int> _successesByMethod = new();

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1000); // JitTracingKeyword
    }

    private static string Field(EventWrittenEventArgs e, string name)
    {
        ReadOnlyCollection<string> names = e.PayloadNames;
        int index = names?.IndexOf(name) ?? -1;
        return index >= 0 ? e.Payload[index]?.ToString() ?? "" : "";
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        string name = e.EventName;
        if (name is null || !name.StartsWith("MethodJitInlining", StringComparison.Ordinal)) return;
        string compiled = Field(e, "MethodBeingCompiledName");
        if (!compiled.StartsWith("evm_", StringComparison.Ordinal)) return;
        string inlinee = $"{Field(e, "InlineeNamespace")}::{Field(e, "InlineeName")}";
        if (name.Contains("Failed"))
        {
            _failures.AddOrUpdate($"{Field(e, "FailReason")} | {inlinee}", 1, static (_, v) => v + 1);
            _failuresByMethod.AddOrUpdate(compiled, 1, static (_, v) => v + 1);
        }
        else
        {
            _successes.AddOrUpdate(inlinee, 1, static (_, v) => v + 1);
            _successesByMethod.AddOrUpdate(compiled, 1, static (_, v) => v + 1);
        }
    }

    public void Report()
    {
        Console.WriteLine($"jit inlining in evm_ chunks: {_successes.Values.Sum()} inlined, {_failures.Values.Sum()} not inlined");
        foreach ((string key, int count) in _failures.OrderByDescending(static kv => kv.Value).Take(40))
            Console.WriteLine($"  fail {count,6} {key}");
        foreach ((string key, int count) in _successes.OrderByDescending(static kv => kv.Value).Take(15))
            Console.WriteLine($"  ok   {count,6} {key}");
        foreach (string method in _successesByMethod.Keys.Union(_failuresByMethod.Keys).OrderBy(static m => m))
            Console.WriteLine($"  {method}: {_successesByMethod.GetValueOrDefault(method)} inlined, {_failuresByMethod.GetValueOrDefault(method)} not");
    }
}
