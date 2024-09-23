
// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis.StatsAnalyzer;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Evm.Tracing.OpcodeStats;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.State;
using System.Linq;
using NUnit.Framework;
using System;

namespace Nethermind.Evm.Test.Tracing.OpcodeStats;

[TestFixture]
public class OpcodeStatsTracerTests : VirtualMachineTestsBase
{
    private static readonly JsonSerializerOptions SerializerOptions = EthereumJsonSerializer.JsonOptionsIndented;

    private string ExecuteStatsTrace(byte[] code, string? tracerConfig = null)
    {

        CMSketch sketch = new CMSketchBuilder().SetBuckets(1000).SetHashFunctions(4).Build();
        StatsAnalyzer analyzer = new StatsAnalyzerBuilder().SetBufferSizeForSketches(2).SetTopN(100).SetCapacity(100000)
                                      .SetMinSupport(1).SetSketchResetOrReuseThreshold(0.001).SetSketch(sketch).Build();
        OpcodeStatsTracer tracer = new(100000, analyzer);

        ExecuteBlock(tracer, code, MainnetSpecProvider.CancunActivation);
        var traces = tracer.BuildResult();


        return JsonSerializer.Serialize(traces.ElementAtOrDefault(0), SerializerOptions)
            .ReplaceLineEndings("\n");
    }


    [Test]
    public void Test_StatsTrace()
    {
        byte[] code = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .PushData(0)
            .Done;

        string callTrace = ExecuteStatsTrace(code);
        const string expectedCallTrace = """
            {
              "initialBlockNumber": "0x0",
              "currentBlockNumber": "0x0",
              "errorPerItem": 0.006,
              "confidence": 0.9375,
              "stats": {
                "pattern": "PUSH1 PUSH1",
                "bytes": [
                  96,
                  96
                ],
                "count": "0x2"
              },
              {
                "pattern": "PUSH1 PUSH1 PUSH1",
                "bytes": [
                  96,
                  96,
                  96
                ],
                "count": "0x1"
              }
            }
            """;
       Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }
}
