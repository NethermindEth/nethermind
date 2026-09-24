// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[TestFixture]
public class GethLikePrestateTracerTests : VirtualMachineTestsBase
{
    [Test]
    public void Execute_WhenPrestateIsFiltered_ObservesOnlyRequiredOpcodes([Values] bool fullTrace)
    {
        RecordingPrestateTracer tracer = new(TestState);
        using ITxTracer executionTracer = new CancellationTxTracer(new CompositeTxTracer(
            tracer, fullTrace ? new FullInstructionTracer() : NullTxTracer.Instance));
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .PushData(2)
            .Op(Instruction.ADD)
            .PushData(0)
            .Op(Instruction.SLOAD)
            .Op(Instruction.STOP)
            .Done;

        Execute(executionTracer, code, MainnetSpecProvider.CancunActivation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Operations, Does.Contain(Instruction.SLOAD));
            Assert.That(tracer.Operations.Contains(Instruction.ADD), Is.EqualTo(fullTrace));
            Assert.That(tracer.Error, Is.Null);
        }
    }

    [Test]
    public void Execute_WhenExcludedOpcodeFails_ReportsTheError()
    {
        using RecordingPrestateTracer tracer = new(TestState);

        Execute(tracer, Prepare.EvmCode.Op(Instruction.ADD).Done, MainnetSpecProvider.CancunActivation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Operations, Does.Not.Contain(Instruction.ADD));
            Assert.That(tracer.Error, Is.EqualTo(EvmExceptionType.StackUnderflow));
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = EthereumJsonSerializer.JsonOptionsIndented;
    private const string DiffMode = """{"diffMode":true}""";
    private const string PrestateMode = """{"diffMode":false}""";
    private const string? NoConfig = null;

    [TestCaseSource(nameof(CaptureCases))]
    public void StartOperation_WhenOpcodeChanges_CapturesOnlyRequiredInputs(Instruction opcode, bool stack, bool memory)
    {
        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions() with { EnableReturnData = true },
            Hash256.Zero, TestItem.AddressA, TestItem.AddressB);
        using ITxTracer outer = new CancellationTxTracer(new CompositeTxTracer(tracer));
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            null!, TestItem.AddressB, TestItem.AddressA, null, 0, UInt256.Zero, default);

        outer.StartOperation(0, Instruction.CREATE2, 100, environment);
        outer.StartOperation(1, opcode, 100, environment);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.IsTracingStack, Is.EqualTo(stack));
            Assert.That(tracer.IsTracingMemory, Is.EqualTo(memory));
            Assert.That(tracer.IsTracingOpLevelStorage, Is.False);
            Assert.That(tracer.IsTracingReturnData, Is.False);
            Assert.That(tracer.IsTracingRefunds, Is.False);
            Assert.That(outer.IsTracingStack, Is.EqualTo(stack));
            Assert.That(outer.IsTracingMemory, Is.EqualTo(memory));
        }
    }

    private static IEnumerable<TestCaseData> CaptureCases()
    {
        yield return new TestCaseData(Instruction.ADD, false, false);
        yield return new TestCaseData(Instruction.CREATE2, true, true);
        Instruction[] stackInstructions =
        [
            Instruction.SLOAD, Instruction.SSTORE, Instruction.BALANCE,
            Instruction.EXTCODECOPY, Instruction.EXTCODEHASH, Instruction.EXTCODESIZE, Instruction.SELFDESTRUCT,
            Instruction.CALL, Instruction.CALLCODE, Instruction.STATICCALL, Instruction.DELEGATECALL, Instruction.CREATE,
        ];
        foreach (Instruction instruction in stackInstructions)
        {
            yield return new TestCaseData(instruction, true, false);
        }
    }

    [Test]
    public void SetOperationStack_WhenAnOperationHasErrored_CapturesNothingMore([Values] bool errored)
    {
        using NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(),
            Hash256.Zero, TestItem.AddressA, TestItem.AddressB);
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            null!, TestItem.AddressB, TestItem.AddressA, null, 0, UInt256.Zero, default);

        tracer.StartOperation(0, Instruction.SLOAD, 100, environment);
        if (errored)
        {
            tracer.ReportOperationError(EvmExceptionType.OutOfGas);
            // The frame that halted unwinds, but its caller keeps executing unrelated opcodes
            tracer.StartOperation(1, Instruction.ADD, 100, environment);
        }

        tracer.SetOperationStack(new TraceStack(new byte[EvmStack.WordSize]));

        NativePrestateTracerAccount account =
            ((Dictionary<AddressAsKey, NativePrestateTracerAccount>)tracer.BuildResult().CustomTracerResult!.Value)[TestItem.AddressB];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.IsTracingStack, Is.EqualTo(!errored));
            Assert.That(account.Storage?.Count ?? 0, Is.EqualTo(errored ? 0 : 1));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("00")]
    [TestCase("ef01000000000000000000000000000000000000001234")]
    public void Prestate_serializes_nonempty_code_hash(string? code)
    {
        if (code is not null)
        {
            TestState.CreateAccount(TestItem.AddressB, 1);
            TestState.InsertCode(TestItem.AddressB, Bytes.FromHexString(code), Spec);
        }
        using NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(),
            Hash256.Zero, TestItem.AddressA, TestItem.AddressB);

        JsonElement result = JsonSerializer.SerializeToElement(tracer.BuildResult().CustomTracerResult!.Value, SerializerOptions);
        JsonElement account = result.GetProperty(TestItem.AddressB.ToString());
        bool hasCode = !string.IsNullOrEmpty(code);
        Assert.That(account.TryGetProperty("codeHash", out JsonElement codeHash), Is.EqualTo(hasCode));
        if (hasCode)
            Assert.That(codeHash.GetString(), Is.EqualTo(Keccak.Compute(Bytes.FromHexString(code!)).ToString()));
    }

    [TestCase("00", "00")]
    [TestCase("00", "6000")]
    [TestCase("", "00")]
    [TestCase("00", "")]
    [TestCase("", "")]
    [TestCase("00", null)]
    [TestCase("", null)]
    [TestCase("ef01000000000000000000000000000000000000001234", "")]
    public void Diff_serializes_changed_code_and_hash(string before, string? after)
    {
        byte[] beforeCode = Bytes.FromHexString(before);
        TestState.CreateAccount(TestItem.AddressB, 1);
        TestState.InsertCode(TestItem.AddressB, beforeCode, Spec);
        using NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(DiffMode),
            Hash256.Zero, TestItem.AddressA, TestItem.AddressB);
        if (after is null)
            TestState.DeleteAccount(TestItem.AddressB);
        else
        {
            TestState.InsertCode(TestItem.AddressB, Bytes.FromHexString(after), Spec);
            TestState.SubtractFromBalance(TestItem.AddressB, 1, Spec, out _);
        }
        tracer.MarkAsSuccess(TestItem.AddressB, default, [], []);

        JsonElement result = JsonSerializer.SerializeToElement(tracer.BuildResult().CustomTracerResult!.Value, SerializerOptions);
        JsonElement pre = result.GetProperty("pre").GetProperty(TestItem.AddressB.ToString());
        JsonElement post = result.GetProperty("post").GetProperty(TestItem.AddressB.ToString());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pre.TryGetProperty("codeHash", out JsonElement preHash), Is.EqualTo(before.Length > 0));
            if (before.Length > 0)
                Assert.That(preHash.GetString(), Is.EqualTo(Keccak.Compute(beforeCode).ToString()));
            Assert.That(post.TryGetProperty("codeHash", out JsonElement postHash), Is.EqualTo(before != after));
            if (before != after)
                Assert.That(postHash.GetString(), Is.EqualTo((after is null ? Hash256.Zero : Keccak.Compute(Bytes.FromHexString(after))).ToString()));
            bool codeChanged = before != (after ?? "");
            Assert.That(post.TryGetProperty("code", out JsonElement postCode), Is.EqualTo(codeChanged));
            if (codeChanged)
                Assert.That(postCode.GetString(), Is.EqualTo("0x" + after));
        }
    }

    private static void AssertTrace(GethLikeTxTrace trace, string expectedTrace) =>
        Assert.That(JsonSerializer.Serialize(trace.CustomTracerResult?.Value, SerializerOptions),
            Is.EqualTo(expectedTrace.ReplaceLineEndings(SerializerOptions.NewLine)));

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
              "codeHash": "0x3e7b60198c46be6d3baf6996b6b0699d2750bb35ce4e9f495eefe94dd40c1525",
              "storage": {
                "0x0000000000000000000000000000000000000000000000000000000000000000": "0x0000000000000000000000000000000000000000000000000000000000a01234",
                "0x0000000000000000000000000000000000000000000000000000000000000020": "0x0000000000000000000000000000000000000000000000000000000000b15678"
              }
            }
          }
        }
        """;

    [TestCase(NoConfig, ExpectedSStorePrestateTrace, false)]
    [TestCase(PrestateMode, ExpectedSStorePrestateTrace, false)]
    [TestCase(DiffMode, ExpectedSStoreDiffModeTrace, false)]
    [TestCase(NoConfig, ExpectedSStorePrestateTrace, true)]
    [TestCase(PrestateMode, ExpectedSStorePrestateTrace, true)]
    [TestCase(DiffMode, ExpectedSStoreDiffModeTrace, true)]
    public void Test_PrestateTrace_SStore(string? config, string expectedTrace, bool wrapped)
    {
        TestState.CreateAccount(Address.Zero, 100.Ether);
        StorageCell storageCell = new(TestItem.AddressB, 32);
        byte[] storageData = Bytes.FromHexString("123456789abcdef");
        TestState.Set(storageCell, new UInt256(storageData, isBigEndian: true));

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(config), Hash256.Zero, TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace trace = ExecutePrestate(tracer, SStore, wrapped);
        AssertTrace(trace, expectedTrace);
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
            "code": "0x7f7f000000000000000000000000000000000000000000000000000000000000006000527f0060005260036000f30000000000000000000000000000000000000000000000602052602960006000f000",
            "codeHash": "0xed25a1b0948283ea4844073b1cdb8e1bc6624420c646ad850059b2b8086faaa9"
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
              "code": "0x60006000600060007376e68a8696537e4141926f3e528733af9e237d6961c350f400",
              "codeHash": "0xf0a0df4051fb30bcef4898ac511f8cf40e2f6fb5279ef80ce84d13051ab2138e"
            },
            "0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3": {
              "nonce": 1,
              "code": "0x000000",
              "codeHash": "0x99ff0d9125e1fc9531a11262e15aeb2c60509a078c4cc4c64cefdfb06ff68647"
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

        TestState.CreateAccount(Address.Zero, 100.Ether);
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, createCode, Spec);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(config), Hash256.Zero, TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace trace = Execute(
                tracer,
                nestedCode,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();

        AssertTrace(trace, expectedTrace);
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
            "code": "0x7f7f010203000000000000000000000000000000000000000000000000000000006000527f0060005260036000f3000000000000000000000000000000000000000000000060205262040506602960006000f5",
            "codeHash": "0xb2139a4067bf64811bda94f4d73a4a9fdb7d5015e57a92274e9e009e48354dc5"
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
              "code": "0x7f7f010203000000000000000000000000000000000000000000000000000000006000527f0060005260036000f3000000000000000000000000000000000000000000000060205262040506602960006000f5",
              "codeHash": "0xb2139a4067bf64811bda94f4d73a4a9fdb7d5015e57a92274e9e009e48354dc5"
            }
          },
          "post": {
            "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
              "balance": "0x56bc75e2d630f241c",
              "nonce": 1
            },
            "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358": {
              "balance": "0x56bc75e2d63100001",
              "code": "0x600060006000600060007376e68a8696537e4141926f3e528733af9e237d6961c350f1",
              "codeHash": "0x446a918ac490932cbcc6ed9f50fedcaef0b253ba7b36185cfffcdb291e642b2b"
            },
            "0x76e68a8696537e4141926f3e528733af9e237d69": {
              "nonce": 1
            },
            "0x02caaf71b895896a4d9159943eae74efb6a58238": {
              "nonce": 1,
              "code": "0x010203",
              "codeHash": "0xf1885eda54b7a053318cd41e2093220dab15d65381b1157a3633a83bfd5c9239"
            }
          }
        }
        """;

    [TestCase(NoConfig, ExpectedCreate2PrestateTrace, false)]
    [TestCase(PrestateMode, ExpectedCreate2PrestateTrace, false)]
    [TestCase(DiffMode, ExpectedCreate2DiffModeTrace, false)]
    [TestCase(NoConfig, ExpectedCreate2PrestateTrace, true)]
    [TestCase(PrestateMode, ExpectedCreate2PrestateTrace, true)]
    [TestCase(DiffMode, ExpectedCreate2DiffModeTrace, true)]
    public void Test_PrestateTrace_Create2(string? config, string expectedTrace, bool wrapped)
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

        TestState.CreateAccount(Address.Zero, 100.Ether);
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, createCode, Spec);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(config), Hash256.Zero, TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace trace = ExecutePrestate(tracer, code, wrapped);

        AssertTrace(trace, expectedTrace);
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
              "code": "0x7f00000000000000000000000076e68a8696537e4141926f3e528733af9e237d693100",
              "codeHash": "0xbe804ee80a769a2c28b7749b62bac22da8bfa2ec1a64f5bc091233d588838a78"
            }
          }
        }
        """;

    [TestCase(NoConfig, ExpectedExistingAccountPrestateTrace)]
    [TestCase(PrestateMode, ExpectedExistingAccountPrestateTrace)]
    [TestCase(DiffMode, ExpectedExistingAccountDiffModeTrace)]
    public void Test_PrestateTrace_ExistingAccount(string? config, string expectedTrace)
    {
        TestState.CreateAccount(Address.Zero, 100.Ether);
        TestState.CreateAccount(TestItem.AddressC, 5.Ether);
        TestState.IncrementNonce(TestItem.AddressC);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(config), Hash256.Zero, TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace trace = Execute(
                tracer,
                Balance,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();

        AssertTrace(trace, expectedTrace);
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
            },
            "0x24cd2edba056b7c654a50e8201b619d4f624fdda": {
              "balance": "0x0"
            },
            "0x76e68a8696537e4141926f3e528733af9e237d69": {
              "balance": "0x0"
            }
          },
          "post": {
            "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
              "balance": "0x56bc75e2d630fa3cc",
              "nonce": 1
            },
            "0x24cd2edba056b7c654a50e8201b619d4f624fdda": {
              "codeHash": "0x0000000000000000000000000000000000000000000000000000000000000000"
            },
            "0x76e68a8696537e4141926f3e528733af9e237d69": {
              "codeHash": "0x0000000000000000000000000000000000000000000000000000000000000000"
            }
          }
        }
        """;

    [TestCase(NoConfig, ExpectedEmptyToPrestateTrace)]
    [TestCase(PrestateMode, ExpectedEmptyToPrestateTrace)]
    [TestCase(DiffMode, ExpectedEmptyToDiffModeTrace)]
    public void Test_PrestateTrace_EmptyTo(string? config, string expectedTrace)
    {
        TestState.CreateAccount(Address.Zero, 100.Ether);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(config), Hash256.Zero, TestItem.AddressA, null, Address.Zero);
        GethLikeTxTrace trace = Execute(
                tracer,
                Balance,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();

        AssertTrace(trace, expectedTrace);
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
            "0x24cd2edba056b7c654a50e8201b619d4f624fdda": {
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
            },
            "0x24cd2edba056b7c654a50e8201b619d4f624fdda": {
              "codeHash": "0x0000000000000000000000000000000000000000000000000000000000000000"
            }
          }
        }
        """;

    [TestCase(NoConfig, ExpectedSelfDestructPrestateTrace)]
    [TestCase(PrestateMode, ExpectedEmptyToPrestateTrace)]
    [TestCase(DiffMode, ExpectedSelfDestructDiffModeTrace)]
    public void Test_PrestateTrace_SelfDestruct(string? config, string expectedTrace)
    {
        TestState.CreateAccount(Address.Zero, 100.Ether);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(config), Hash256.Zero, TestItem.AddressA, null, Address.Zero);
        GethLikeTxTrace trace = Execute(
                tracer,
                SelfDestruct,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();

        AssertTrace(trace, expectedTrace);
    }

    private GethLikeTxTrace ExecutePrestate(NativePrestateTracer tracer, byte[] code, bool wrapped)
    {
        using ITxTracer executionTracer = wrapped
            ? new CancellationTxTracer(new CompositeTxTracer(tracer, new FullInstructionTracer()), default)
            : tracer;
        Execute(executionTracer, code, MainnetSpecProvider.CancunActivation);
        return tracer.BuildResult();
    }

    private sealed class FullInstructionTracer : TxTracer
    {
        public override bool IsTracingInstructions => true;
    }

    private sealed class RecordingPrestateTracer(IWorldState worldState)
        : NativePrestateTracer(worldState, GetGethTraceOptions(), Hash256.Zero, TestItem.AddressA, TestItem.AddressB)
    {
        public List<Instruction> Operations { get; } = [];
        public EvmExceptionType? Error { get; private set; }

        public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
        {
            Operations.Add(opcode);
            base.StartOperation(pc, opcode, gas, env);
        }

        public override void ReportOperationError(EvmExceptionType error)
        {
            Error = error;
            base.ReportOperationError(error);
        }
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
