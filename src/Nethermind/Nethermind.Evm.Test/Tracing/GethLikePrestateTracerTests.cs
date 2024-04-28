// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[TestFixture]
public class GethLikePrestateTracerTests : VirtualMachineTestsBase
{
    private static readonly JsonSerializerOptions SerializerOptions = EthereumJsonSerializer.JsonOptionsIndented;
    private const string DiffMode = """{"diffMode":true}""";

    private static GethTraceOptions GetGethTraceOptions(string? config = null) => GethTraceOptions.Default with
    {
        Tracer = NativePrestateTracer.PrestateTracer,
        TracerConfig = config is not null ? JsonSerializer.Deserialize<JsonElement>(config) : null
    };

    [Test]
    public void Test_PrestateTrace_SStore()
    {
        TestState.CreateAccount(Address.Zero, 100.Ether());
        StorageCell storageCell = new StorageCell(TestItem.AddressB, 32);
        byte[] storageData = Bytes.FromHexString("123456789abcdef");
        TestState.Set(storageCell, storageData);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(), TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace prestateTrace = Execute(
                tracer,
                SStore,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();
        const string expectedPrestateTrace = """
{
  "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
    "balance": "0x0"
  },
  "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358": {
    "balance": "0x0",
    "storage": {
      "0x0000000000000000000000000000000000000000000000000000000000000000": "0x0000000000000000000000000000000000000000000000000000000000000000",
      "0x0000000000000000000000000000000000000000000000000000000000000020": "0x0000000000000000000000000000000000000000000000000123456789abcdef"
    }
  },
  "0x0000000000000000000000000000000000000000": {
    "balance": "0x56bc75e2d63100000"
  }
}
""";
        Assert.That(JsonSerializer.Serialize(prestateTrace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedPrestateTrace));
    }

    [Test]
    public void Test_PrestateTrace_SStore_DiffMode()
    {
        TestState.CreateAccount(Address.Zero, 100.Ether());
        StorageCell storageCell = new StorageCell(TestItem.AddressB, 32);
        byte[] storageData = Bytes.FromHexString("123456789abcdef");
        TestState.Set(storageCell, storageData);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(DiffMode), TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace prestateTrace = Execute(
                tracer,
                SStore,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();
        const string expectedPrestateTrace = """
{
  "pre": {
    "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
      "balance": "0x0"
    },
    "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358": {
      "balance": "0x0",
      "storage": {
        "0x0000000000000000000000000000000000000000000000000000000000000020": "0x0000000000000000000000000000000000000000000000000123456789abcdef"
      }
    }
  },
  "post": {
    "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
      "balance": "0x56bc75e2d630f440f",
      "nonce": 1
    },
    "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358": {
      "balance": "0x56bc75e2d63100001",
      "code": "0x7f0000000000000000000000000000000000000000000000000000000000a012346000557f0000000000000000000000000000000000000000000000000000000000b1567860205500",
      "storage": {
        "0x0000000000000000000000000000000000000000000000000000000000000000": "0x0000000000000000000000000000000000000000000000000000000000a01234",
        "0x0000000000000000000000000000000000000000000000000000000000000020": "0x0000000000000000000000000000000000000000000000000000000000b15678"
      }
    }
  }
}
""";
        Assert.That(JsonSerializer.Serialize(prestateTrace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedPrestateTrace));
    }

    [Test]
    public void Test_PrestateTrace_NestedCalls()
    {
        byte[] deployedCode = new byte[3];
        byte[] initCode = Prepare.EvmCode
            .ForInitOf(deployedCode)
            .Done;
        byte[] createCode = Prepare.EvmCode
            .Create(initCode, 0)
            .Op(Instruction.STOP)
            .Done;
        byte[] nestedCode = Prepare.EvmCode
            .DelegateCall(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;

        TestState.CreateAccount(Address.Zero, 100.Ether());
        TestState.CreateAccount(TestItem.AddressC, 1.Ether());
        TestState.InsertCode(TestItem.AddressC, createCode, Spec);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(), TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace prestateTrace = Execute(
                tracer,
                nestedCode,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();
        const string expectedPrestateTrace = """
{
  "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
    "balance": "0x0"
  },
  "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358": {
    "balance": "0x0"
  },
  "0x0000000000000000000000000000000000000000": {
    "balance": "0x56bc75e2d63100000"
  },
  "0x76e68a8696537e4141926f3e528733af9e237d69": {
    "balance": "0xde0b6b3a7640000",
    "code": "0x7f7f000000000000000000000000000000000000000000000000000000000000006000527f0060005260036000f30000000000000000000000000000000000000000000000602052602960006000f000"
  },
  "0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3": {
    "balance": "0x0"
  }
}
""";
        Assert.That(JsonSerializer.Serialize(prestateTrace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedPrestateTrace));
    }

    [Test]
    public void Test_PrestateTrace_NestedCalls_DiffMode()
    {
        byte[] deployedCode = new byte[3];
        byte[] initCode = Prepare.EvmCode
            .ForInitOf(deployedCode)
            .Done;
        byte[] createCode = Prepare.EvmCode
            .Create(initCode, 0)
            .Op(Instruction.STOP)
            .Done;
        byte[] nestedCode = Prepare.EvmCode
            .DelegateCall(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;

        TestState.CreateAccount(Address.Zero, 100.Ether());
        TestState.CreateAccount(TestItem.AddressC, 1.Ether());
        TestState.InsertCode(TestItem.AddressC, createCode, Spec);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(DiffMode), TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace prestateTrace = Execute(
                tracer,
                nestedCode,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();
        const string expectedPrestateTrace = """
{
  "pre": {
    "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
      "balance": "0x0"
    },
    "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358": {
      "balance": "0x0"
    }
  },
  "post": {
    "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
      "balance": "0x56bc75e2d630f242e",
      "nonce": 1
    },
    "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358": {
      "balance": "0x56bc75e2d63100001",
      "nonce": 1,
      "code": "0x60006000600060007376e68a8696537e4141926f3e528733af9e237d6961c350f400"
    },
    "0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3": {
      "nonce": 1,
      "code": "0x000000"
    }
  }
}
""";
        Assert.That(JsonSerializer.Serialize(prestateTrace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedPrestateTrace));
    }

    [Test]
    public void Test_PrestateTrace_Create2()
    {
        byte[] salt = { 4, 5, 6 };
        byte[] deployedCode = { 1, 2, 3 };
        byte[] initCode = Prepare.EvmCode
            .ForInitOf(deployedCode).Done;
        byte[] createCode = Prepare.EvmCode
            .Create2(initCode, salt, 0).Done;
        byte[] code = Prepare.EvmCode
            .Call(TestItem.AddressC, 50000)
            .Done;

        TestState.CreateAccount(Address.Zero, 100.Ether());
        TestState.CreateAccount(TestItem.AddressC, 1.Ether());
        TestState.InsertCode(TestItem.AddressC, createCode, Spec);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(), TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace prestateTrace = Execute(
                tracer,
                code,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();
        const string expectedPrestateTrace = """
{
  "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
    "balance": "0x0"
  },
  "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358": {
    "balance": "0x0"
  },
  "0x0000000000000000000000000000000000000000": {
    "balance": "0x56bc75e2d63100000"
  },
  "0x76e68a8696537e4141926f3e528733af9e237d69": {
    "balance": "0xde0b6b3a7640000",
    "code": "0x7f7f010203000000000000000000000000000000000000000000000000000000006000527f0060005260036000f3000000000000000000000000000000000000000000000060205262040506602960006000f5"
  },
  "0x02caaf71b895896a4d9159943eae74efb6a58238": {
    "balance": "0x0"
  }
}
""";
        Assert.That(JsonSerializer.Serialize(prestateTrace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedPrestateTrace));
    }

    [Test]
    public void Test_PrestateTrace_Create2_DiffMode()
    {
        byte[] salt = { 4, 5, 6 };
        byte[] deployedCode = { 1, 2, 3 };
        byte[] initCode = Prepare.EvmCode
            .ForInitOf(deployedCode).Done;
        byte[] createCode = Prepare.EvmCode
            .Create2(initCode, salt, 0).Done;
        byte[] code = Prepare.EvmCode
            .Call(TestItem.AddressC, 50000)
            .Done;

        TestState.CreateAccount(Address.Zero, 100.Ether());
        TestState.CreateAccount(TestItem.AddressC, 1.Ether());
        TestState.InsertCode(TestItem.AddressC, createCode, Spec);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(DiffMode), TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace prestateTrace = Execute(
                tracer,
                code,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();
        const string expectedPrestateTrace = """
{
  "pre": {
    "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
      "balance": "0x0"
    },
    "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358": {
      "balance": "0x0"
    },
    "0x76e68a8696537e4141926f3e528733af9e237d69": {
      "balance": "0xde0b6b3a7640000",
      "code": "0x7f7f010203000000000000000000000000000000000000000000000000000000006000527f0060005260036000f3000000000000000000000000000000000000000000000060205262040506602960006000f5"
    }
  },
  "post": {
    "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
      "balance": "0x56bc75e2d630f241c",
      "nonce": 1
    },
    "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358": {
      "balance": "0x56bc75e2d63100001",
      "code": "0x600060006000600060007376e68a8696537e4141926f3e528733af9e237d6961c350f1"
    },
    "0x76e68a8696537e4141926f3e528733af9e237d69": {
      "nonce": 1
    },
    "0x02caaf71b895896a4d9159943eae74efb6a58238": {
      "nonce": 1,
      "code": "0x010203"
    }
  }
}
""";
        Assert.That(JsonSerializer.Serialize(prestateTrace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedPrestateTrace));
    }

    [Test]
    public void Test_PrestateTrace_ExistingAccount()
    {
        TestState.CreateAccount(Address.Zero, 100.Ether());
        TestState.CreateAccount(TestItem.AddressC, 5.Ether());
        TestState.IncrementNonce(TestItem.AddressC);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(), TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace prestateTrace = Execute(
                tracer,
                Balance,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();
        const string expectedPrestateTrace = """
{
  "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
    "balance": "0x0"
  },
  "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358": {
    "balance": "0x0"
  },
  "0x0000000000000000000000000000000000000000": {
    "balance": "0x56bc75e2d63100000"
  },
  "0x76e68a8696537e4141926f3e528733af9e237d69": {
    "balance": "0x4563918244f40000",
    "nonce": 1
  }
}
""";
        Assert.That(JsonSerializer.Serialize(prestateTrace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedPrestateTrace));
    }

    [Test]
    public void Test_PrestateTrace_ExistingAccount_DiffMode()
    {
        TestState.CreateAccount(Address.Zero, 100.Ether());
        TestState.CreateAccount(TestItem.AddressC, 5.Ether());
        TestState.IncrementNonce(TestItem.AddressC);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(DiffMode), TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace prestateTrace = Execute(
                tracer,
                Balance,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();
        const string expectedPrestateTrace = """
{
  "pre": {
    "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
      "balance": "0x0"
    },
    "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358": {
      "balance": "0x0"
    }
  },
  "post": {
    "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
      "balance": "0x56bc75e2d630fa3cc",
      "nonce": 1
    },
    "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358": {
      "balance": "0x56bc75e2d63100001",
      "code": "0x7f00000000000000000000000076e68a8696537e4141926f3e528733af9e237d693100"
    }
  }
}
""";
        Assert.That(JsonSerializer.Serialize(prestateTrace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedPrestateTrace));
    }

    [Test]
    public void Test_PrestateTrace_EmptyTo()
    {
        TestState.CreateAccount(Address.Zero, 100.Ether());

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(), TestItem.AddressA, null, Address.Zero);
        GethLikeTxTrace prestateTrace = Execute(
                tracer,
                Balance,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();
        const string expectedPrestateTrace = """
{
  "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
    "balance": "0x0"
  },
  "0x24cd2edba056b7c654a50e8201b619d4f624fdda": {
    "balance": "0x0"
  },
  "0x0000000000000000000000000000000000000000": {
    "balance": "0x56bc75e2d63100000"
  },
  "0x76e68a8696537e4141926f3e528733af9e237d69": {
    "balance": "0x0"
  }
}
""";
        Assert.That(JsonSerializer.Serialize(prestateTrace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedPrestateTrace));
    }

    [Test]
    public void Test_PrestateTrace_EmptyTo_DiffMode()
    {
        TestState.CreateAccount(Address.Zero, 100.Ether());

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(DiffMode), TestItem.AddressA, null, Address.Zero);
        GethLikeTxTrace prestateTrace = Execute(
                tracer,
                Balance,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();
        const string expectedPrestateTrace = """
{
  "pre": {
    "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
      "balance": "0x0"
    }
  },
  "post": {
    "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
      "balance": "0x56bc75e2d630fa3cc",
      "nonce": 1
    }
  }
}
""";
        Assert.That(JsonSerializer.Serialize(prestateTrace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedPrestateTrace));
    }

    [Test]
    public void Test_PrestateTrace_SelfDestruct()
    {
        TestState.CreateAccount(Address.Zero, 100.Ether());

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(), TestItem.AddressA, null, Address.Zero);
        GethLikeTxTrace prestateTrace = Execute(
                tracer,
                SelfDestruct,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();
        const string expectedPrestateTrace = """
{
  "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
    "balance": "0x0"
  },
  "0x24cd2edba056b7c654a50e8201b619d4f624fdda": {
    "balance": "0x0"
  },
  "0x0000000000000000000000000000000000000000": {
    "balance": "0x56bc75e2d63100000"
  },
  "0x76e68a8696537e4141926f3e528733af9e237d69": {
    "balance": "0x0"
  }
}
""";
        Assert.That(JsonSerializer.Serialize(prestateTrace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedPrestateTrace));
    }

    [Test]
    public void Test_PrestateTrace_SelfDestruct_DiffMode()
    {
        TestState.CreateAccount(Address.Zero, 100.Ether());

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(DiffMode), TestItem.AddressA, null, Address.Zero);
        GethLikeTxTrace prestateTrace = Execute(
                tracer,
                SelfDestruct,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();
        const string expectedPrestateTrace = """
{
  "pre": {
    "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
      "balance": "0x0"
    },
    "0x76e68a8696537e4141926f3e528733af9e237d69": {
      "balance": "0x0"
    }
  },
  "post": {
    "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
      "balance": "0x56bc75e2d630f2e9c",
      "nonce": 1
    }
  }
}
""";
        Assert.That(JsonSerializer.Serialize(prestateTrace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedPrestateTrace));
    }

    private static byte[] SStore => Prepare.EvmCode
        .PushData(SampleHexData1.PadLeft(64, '0'))
        .PushData(0)
        .Op(Instruction.SSTORE)
        .PushData(SampleHexData2.PadLeft(64, '0'))
        .PushData(32)
        .Op(Instruction.SSTORE)
        .Op(Instruction.STOP)
        .Done;

    private static byte[] Balance => Prepare.EvmCode
        .PushData(TestItem.AddressC.ToString(false, false).PadLeft(64, '0'))
        .Op(Instruction.BALANCE)
        .Op(Instruction.STOP)
        .Done;

    private static byte[] SelfDestruct => Prepare.EvmCode
        .PushData(TestItem.AddressC.ToString(false, false).PadLeft(64, '0'))
        .Op(Instruction.SELFDESTRUCT)
        .Done;
}
