// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.JsonRpc.Modules.DebugModule;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.All)]
public class DebugTraceCallBatchConcurrencyTests
{
    [TestCase("""[{},"latest",{"tracer":"prestateTracer"}]""", ExpectedResult = true, TestName = "Native tracer")]
    [TestCase("""[{},"latest",{"tracer":"callTracer","tracerConfig":{"onlyTopCall":true}}]""", ExpectedResult = true, TestName = "Native tracer with config")]
    [TestCase("""[{},"latest"]""", ExpectedResult = false, TestName = "No options: struct logs may stream")]
    [TestCase("""[{},"latest",{}]""", ExpectedResult = false, TestName = "No tracer: struct logs may stream")]
    [TestCase("""[{},"latest",{"tracer":"{result:function(){return 1},fault:function(){}}"}]""", ExpectedResult = false, TestName = "JavaScript tracer")]
    [TestCase("""[{},"latest",{"tracer":"callTracer","Tracer":null}]""", ExpectedResult = false, TestName = "Later case alias without a tracer")]
    [TestCase("""[{},"latest",{"tracer":"callTracer","Tracer":"{result:function(){return 1},fault:function(){}}"}]""", ExpectedResult = false, TestName = "Later case alias with a JavaScript tracer")]
    [TestCase("""[{},"latest",{"Tracer":"callTracer"}]""", ExpectedResult = false, TestName = "Case alias only")]
    [TestCase("""[{},"latest",{"tracer":"callTracer","tracer":"callTracer"}]""", ExpectedResult = false, TestName = "Repeated tracer")]
    [TestCase("""[{},"latest",{"tracer":"\uD800"}]""", ExpectedResult = false, TestName = "Unreadable tracer name")]
    [TestCase("""[{},"latest",{"tracer":"callTracer","\uD800":null}]""", ExpectedResult = false, TestName = "Unreadable property name")]
    [TestCase("""[{},"latest",{"tracer":1}]""", ExpectedResult = false, TestName = "Tracer that is not a string")]
    [TestCase("""{"tracer":"callTracer"}""", ExpectedResult = false, TestName = "Params that are not an array")]
    public bool Runs_concurrently_only_with_one_native_tracer(string parameters) =>
        CanExecuteConcurrently(parameters, debugModuleInstances: 4, windowSize: 4);

    [TestCase(1, 4, ExpectedResult = false)]
    [TestCase(4, 4, ExpectedResult = true)]
    [TestCase(16, 4, ExpectedResult = true)]
    public bool Runs_concurrently_only_while_the_window_fits_the_debug_module_pool(int debugModuleInstances, int windowSize) =>
        CanExecuteConcurrently("""[{},"latest",{"tracer":"prestateTracer"}]""", debugModuleInstances, windowSize);

    private static bool CanExecuteConcurrently(string parameters, int debugModuleInstances, int windowSize)
    {
        using JsonDocument document = JsonDocument.Parse(parameters);
        DebugTraceCallBatchConcurrency strategy = new(new JsonRpcConfig { DebugModuleConcurrentInstances = debugModuleInstances });
        return strategy.CanExecuteConcurrently(new JsonRpcRequest { Method = "debug_traceCall", Params = document.RootElement }, windowSize);
    }
}
