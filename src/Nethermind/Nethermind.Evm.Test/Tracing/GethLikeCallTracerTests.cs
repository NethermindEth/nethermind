// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[TestFixture]
public class GethLikeCallTracerTests : VirtualMachineTestsBase
{
    private static readonly JsonSerializerOptions SerializerOptions = EthereumJsonSerializer.JsonOptionsIndented;
    private const string? WithLog = """{"withLog":true}""";
    private const string? OnlyTopCall = """{"onlyTopCall":true}""";
    private const string? WithLogAndOnlyTopCall = """{"withLog":true,"onlyTopCall":true}""";

    private string ExecuteCallTrace(byte[] code, string? tracerConfig = null)
    {
        (_, Transaction tx) = PrepareTx(MainnetSpecProvider.CancunActivation, 100000, code);
        NativeCallTracer tracer = new NativeCallTracer(tx, GetGethTraceOptions(tracerConfig));

        GethLikeTxTrace callTrace = Execute(
                tracer,
                code,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();
        return JsonSerializer.Serialize(callTrace.CustomTracerResult?.Value, SerializerOptions);
    }

    private static GethTraceOptions GetGethTraceOptions(string? config) => GethTraceOptions.Default with
    {
        Tracer = NativeCallTracer.CallTracer,
        TracerConfig = config is not null ? JsonSerializer.Deserialize<JsonElement>(config) : null
    };

    [Test]
    public void Test_callTrace_SingleCall()
    {
        byte[] code = Prepare.EvmCode
            .PushData(SampleHexData1.PadLeft(64, '0'))
            .PushData(0)
            .Op(Instruction.SSTORE)
            .PushData(SampleHexData2.PadLeft(64, '0'))
            .PushData(32)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        string callTrace = ExecuteCallTrace(code);
        const string expectedCallTrace = """
{
  "type": "CALL",
  "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
  "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
  "value": "0x1",
  "gas": "0x186a0",
  "gasUsed": "0xfebc",
  "input": "0x"
}
""";
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    [Test]
    public void Test_callTrace_NestedCalls()
    {
        byte[] code = CreateNestedCallsCode();
        string callTrace = ExecuteCallTrace(code);
        const string expectedCallTrace = """
{
  "type": "CALL",
  "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
  "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
  "value": "0x1",
  "gas": "0x186a0",
  "gasUsed": "0x16644",
  "input": "0x",
  "calls": [
    {
      "type": "CALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x0",
      "gas": "0xc350",
      "gasUsed": "0x8400",
      "input": "0xa01234",
      "calls": [
        {
          "type": "CREATE",
          "from": "0x76e68a8696537e4141926f3e528733af9e237d69",
          "to": "0xd75a3a95360e44a3874e691fb48d77855f127069",
          "value": "0x0",
          "gas": "0x4513",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    },
    {
      "type": "DELEGATECALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x1",
      "gas": "0xa342",
      "gasUsed": "0x8400",
      "input": "0x",
      "calls": [
        {
          "type": "CREATE",
          "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
          "to": "0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3",
          "value": "0x0",
          "gas": "0x2585",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    }
  ]
}
""";
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    [Test]
    public void Test_callTrace_NestedCalls_WithLog()
    {
        byte[] code = CreateNestedCallsCode();
        string callTrace = ExecuteCallTrace(code, WithLog);
        const string expectedCallTrace = """
{
  "type": "CALL",
  "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
  "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
  "value": "0x1",
  "gas": "0x186a0",
  "gasUsed": "0x16644",
  "input": "0x",
  "logs": [
    {
      "address": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "data": "0x",
      "topics": [],
      "position": "0x2"
    }
  ],
  "calls": [
    {
      "type": "CALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x0",
      "gas": "0xc350",
      "gasUsed": "0x8400",
      "input": "0xa01234",
      "logs": [
        {
          "address": "0x76e68a8696537e4141926f3e528733af9e237d69",
          "data": "0x",
          "topics": ["0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111","0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760"
          ],
          "position": "0x1"
        }
      ],
      "calls": [
        {
          "type": "CREATE",
          "from": "0x76e68a8696537e4141926f3e528733af9e237d69",
          "to": "0xd75a3a95360e44a3874e691fb48d77855f127069",
          "value": "0x0",
          "gas": "0x4513",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    },
    {
      "type": "DELEGATECALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x1",
      "gas": "0xa342",
      "gasUsed": "0x8400",
      "input": "0x",
      "logs": [
        {
          "address": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
          "data": "0x",
          "topics": ["0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111","0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760"
          ],
          "position": "0x1"
        }
      ],
      "calls": [
        {
          "type": "CREATE",
          "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
          "to": "0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3",
          "value": "0x0",
          "gas": "0x2585",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    }
  ]
}
""";
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    [Test]
    public void Test_callTrace_NestedCalls_OnlyTopCall()
    {
        byte[] code = CreateNestedCallsCode();
        string callTrace = ExecuteCallTrace(code, OnlyTopCall);
        const string expectedCallTrace = """
{
  "type": "CALL",
  "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
  "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
  "value": "0x1",
  "gas": "0x186a0",
  "gasUsed": "0x16644",
  "input": "0x"
}
""";
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    [Test]
    public void Test_callTrace_NestedCalls_WithLogsAndOnlyTopCall()
    {
        byte[] code = CreateNestedCallsCode();
        string callTrace = ExecuteCallTrace(code, WithLogAndOnlyTopCall);
        const string expectedCallTrace = """
{
  "type": "CALL",
  "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
  "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
  "value": "0x1",
  "gas": "0x186a0",
  "gasUsed": "0x16644",
  "input": "0x",
  "logs": [
    {
      "address": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "data": "0x",
      "topics": [],
      "position": "0x0"
    }
  ]
}
""";
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    [Test]
    public void Test_callTrace_NestedCalls_RevertParentCall()
    {
        byte[] code = CreateNestedCallsCode(true);
        string callTrace = ExecuteCallTrace(code, WithLog);
        const string expectedCallTrace = """
{
  "type": "CALL",
  "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
  "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
  "value": "0x1",
  "gas": "0x186a0",
  "gasUsed": "0x1664a",
  "input": "0x",
  "error": "execution reverted",
  "calls": [
    {
      "type": "CALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x0",
      "gas": "0xc350",
      "gasUsed": "0x8400",
      "input": "0xa01234",
      "calls": [
        {
          "type": "CREATE",
          "from": "0x76e68a8696537e4141926f3e528733af9e237d69",
          "to": "0xd75a3a95360e44a3874e691fb48d77855f127069",
          "value": "0x0",
          "gas": "0x4513",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    },
    {
      "type": "DELEGATECALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x1",
      "gas": "0xa342",
      "gasUsed": "0x8400",
      "input": "0x",
      "calls": [
        {
          "type": "CREATE",
          "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
          "to": "0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3",
          "value": "0x0",
          "gas": "0x2585",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    }
  ]
}
""";
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    [Test]
    public void Test_callTrace_NestedCalls_RevertInternalCall()
    {
        byte[] code = CreateNestedCallsCode(false, true);
        string callTrace = ExecuteCallTrace(code, WithLog);
        const string expectedCallTrace = """
{
  "type": "CALL",
  "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
  "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
  "value": "0x1",
  "gas": "0x186a0",
  "gasUsed": "0x16650",
  "input": "0x",
  "logs": [
    {
      "address": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "data": "0x",
      "topics": [],
      "position": "0x2"
    }
  ],
  "calls": [
    {
      "type": "CALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x0",
      "gas": "0xc350",
      "gasUsed": "0x8406",
      "input": "0xa01234",
      "error": "execution reverted",
      "logs": [
        {
          "address": "0x76e68a8696537e4141926f3e528733af9e237d69",
          "data": "0x",
          "topics": ["0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111","0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760"
          ],
          "position": "0x1"
        }
      ],
      "calls": [
        {
          "type": "CREATE",
          "from": "0x76e68a8696537e4141926f3e528733af9e237d69",
          "to": "0xd75a3a95360e44a3874e691fb48d77855f127069",
          "value": "0x0",
          "gas": "0x4513",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    },
    {
      "type": "DELEGATECALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x1",
      "gas": "0xa33c",
      "gasUsed": "0x8406",
      "input": "0x",
      "error": "execution reverted",
      "logs": [
        {
          "address": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
          "data": "0x",
          "topics": ["0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111","0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760"
          ],
          "position": "0x1"
        }
      ],
      "calls": [
        {
          "type": "CREATE",
          "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
          "to": "0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3",
          "value": "0x0",
          "gas": "0x257f",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    }
  ]
}
""";
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    [Test]
    public void Test_callTrace_NestedCalls_RevertAllCalls()
    {
        byte[] code = CreateNestedCallsCode(true, true);
        string callTrace = ExecuteCallTrace(code, WithLog);
        const string expectedCallTrace = """
{
  "type": "CALL",
  "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
  "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
  "value": "0x1",
  "gas": "0x186a0",
  "gasUsed": "0x16656",
  "input": "0x",
  "error": "execution reverted",
  "calls": [
    {
      "type": "CALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x0",
      "gas": "0xc350",
      "gasUsed": "0x8406",
      "input": "0xa01234",
      "error": "execution reverted",
      "calls": [
        {
          "type": "CREATE",
          "from": "0x76e68a8696537e4141926f3e528733af9e237d69",
          "to": "0xd75a3a95360e44a3874e691fb48d77855f127069",
          "value": "0x0",
          "gas": "0x4513",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    },
    {
      "type": "DELEGATECALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x1",
      "gas": "0xa33c",
      "gasUsed": "0x8406",
      "input": "0x",
      "error": "execution reverted",
      "calls": [
        {
          "type": "CREATE",
          "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
          "to": "0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3",
          "value": "0x0",
          "gas": "0x257f",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    }
  ]
}
""";
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    private byte[] CreateNestedCallsCode(bool revertParentCall = false, bool revertCreateCall = false)
    {
        byte[] deployedCode = new byte[3];

        byte[] initCode = Prepare.EvmCode.ForInitOf(deployedCode).Done;

        Prepare createCodePrepare = Prepare.EvmCode
            .Create(initCode, 0)
            .Log(0, 0, [TestItem.KeccakA, TestItem.KeccakB]);
        byte[] createCode = revertCreateCall ? createCodePrepare.Revert(0, 0).Done : createCodePrepare.STOP().Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether());
        TestState.InsertCode(TestItem.AddressC, createCode, Spec);
        Prepare callCodePrepare = Prepare.EvmCode
            .CallWithInput(TestItem.AddressC, 50000, SampleHexData1)
            .DelegateCall(TestItem.AddressC, 50000)
            .Log(0, 0);
        return revertParentCall ? callCodePrepare.Revert(0, 0).Done : callCodePrepare.Done;
    }
}
