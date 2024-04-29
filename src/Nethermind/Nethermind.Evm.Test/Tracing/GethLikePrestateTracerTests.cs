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
    private const string PrestateMode = """{"diffMode":false}""";
    private const string? NoConfig = null;

    private static GethTraceOptions GetGethTraceOptions(string? config = null) => GethTraceOptions.Default with
    {
        Tracer = NativePrestateTracer.PrestateTracer,
        TracerConfig = config is not null ? JsonSerializer.Deserialize<JsonElement>(config) : null
    };

    private const string ExpectedSStorePrestateTrace = """
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

    private const string ExpectedSStoreDiffModeTrace = """
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

    [TestCase(NoConfig, ExpectedSStorePrestateTrace)]
    [TestCase(PrestateMode, ExpectedSStorePrestateTrace)]
    [TestCase(DiffMode, ExpectedSStoreDiffModeTrace)]
    public void Test_PrestateTrace_SStore(string? config, string expectedTrace)
    {
        TestState.CreateAccount(Address.Zero, 100.Ether());
        StorageCell storageCell = new StorageCell(TestItem.AddressB, 32);
        byte[] storageData = Bytes.FromHexString("123456789abcdef");
        TestState.Set(storageCell, storageData);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(config), TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace trace = Execute(
                tracer,
                SStore,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();
        Assert.That(JsonSerializer.Serialize(trace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedTrace));
    }

    private const string ExpectedNestedCallsPrestateTrace = """
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

    private const string ExpectedNestedCallsDiffModeTrace = """
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

    [TestCase(NoConfig, ExpectedNestedCallsPrestateTrace)]
    [TestCase(PrestateMode, ExpectedNestedCallsPrestateTrace)]
    [TestCase(DiffMode, ExpectedNestedCallsDiffModeTrace)]
    public void Test_PrestateTrace_NestedCalls(string? config, string expectedTrace)
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

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(config), TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace trace = Execute(
                tracer,
                nestedCode,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();

        Assert.That(JsonSerializer.Serialize(trace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedTrace));
    }

    private const string ExpectedCreate2PrestateTrace = """
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

    private const string ExpectedCreate2DiffModeTrace = """
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

    [TestCase(NoConfig, ExpectedCreate2PrestateTrace)]
    [TestCase(PrestateMode, ExpectedCreate2PrestateTrace)]
    [TestCase(DiffMode, ExpectedCreate2DiffModeTrace)]
    public void Test_PrestateTrace_Create2(string? config, string expectedTrace)
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

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(config), TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace trace = Execute(
                tracer,
                code,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();

        Assert.That(JsonSerializer.Serialize(trace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedTrace));
    }

    private const string ExpectedExistingAccountPrestateTrace = """
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

    private const string ExpectedExistingAccountDiffModeTrace = """
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

    [TestCase(NoConfig, ExpectedExistingAccountPrestateTrace)]
    [TestCase(PrestateMode, ExpectedExistingAccountPrestateTrace)]
    [TestCase(DiffMode, ExpectedExistingAccountDiffModeTrace)]
    public void Test_PrestateTrace_ExistingAccount(string? config, string expectedTrace)
    {
        TestState.CreateAccount(Address.Zero, 100.Ether());
        TestState.CreateAccount(TestItem.AddressC, 5.Ether());
        TestState.IncrementNonce(TestItem.AddressC);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(config), TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace trace = Execute(
                tracer,
                Balance,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();

        Assert.That(JsonSerializer.Serialize(trace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedTrace));
    }

    private const string ExpectedEmptyToPrestateTrace = """
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

    private const string ExpectedEmptyToDiffModeTrace = """
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

    [TestCase(NoConfig, ExpectedEmptyToPrestateTrace)]
    [TestCase(PrestateMode, ExpectedEmptyToPrestateTrace)]
    [TestCase(DiffMode, ExpectedEmptyToDiffModeTrace)]
    public void Test_PrestateTrace_EmptyTo(string? config, string expectedTrace)
    {
        TestState.CreateAccount(Address.Zero, 100.Ether());

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(config), TestItem.AddressA, null, Address.Zero);
        GethLikeTxTrace trace = Execute(
                tracer,
                Balance,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();

        Assert.That(JsonSerializer.Serialize(trace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedTrace));
    }

    private const string ExpectedSelfDestructPrestateTrace = """
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

    private const string ExpectedSelfDestructDiffModeTrace = """
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

    [TestCase(NoConfig, ExpectedSelfDestructPrestateTrace)]
    [TestCase(PrestateMode, ExpectedEmptyToPrestateTrace)]
    [TestCase(DiffMode, ExpectedSelfDestructDiffModeTrace)]
    public void Test_PrestateTrace_SelfDestruct(string? config, string expectedTrace)
    {
        TestState.CreateAccount(Address.Zero, 100.Ether());

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(config), TestItem.AddressA, null, Address.Zero);
        GethLikeTxTrace trace = Execute(
                tracer,
                SelfDestruct,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();

        Assert.That(JsonSerializer.Serialize(trace.CustomTracerResult?.Value, SerializerOptions), Is.EqualTo(expectedTrace));
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
