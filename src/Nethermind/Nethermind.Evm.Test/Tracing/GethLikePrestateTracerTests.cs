// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Specs.Forks;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[TestFixture]
public class GethLikePrestateTracerTests : VirtualMachineTestsBase
{
    private static IEnumerable<TestCaseData> AccountPrimitiveCases()
    {
        yield return new TestCaseData(null, "{}").SetName("{m}: Omitted");
        foreach (string nonce in new[]
        {
            "0",
            "300",
            "18446744073709551615",
            "18446744073709551616",
            "115792089237316195423570985008687907853269984665640564039457584007913129639935"
        })
        {
            yield return new TestCaseData(UInt256.Parse(nonce), "{\"balance\":\"0xff\",\"nonce\":" + nonce + "}")
                .SetName("{m}: " + nonce);
        }
    }

    [TestCaseSource(nameof(AccountPrimitiveCases))]
    public void Write_WhenAccountPrimitivesArePresent_WritesHexBalanceAndDecimalNonce(UInt256? nonce, string expected)
    {
        NativePrestateTracerAccount account = new()
        {
            Balance = nonce is not null ? (UInt256)255 : null,
            Nonce = nonce
        };
        NumberConversion previousValue = ForcedNumberConversion.Value;
        try
        {
            ForcedNumberConversion.Value = NumberConversion.Raw;

            string json = JsonSerializer.Serialize(account, new JsonSerializerOptions(SerializerOptions) { WriteIndented = false });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(json, Is.EqualTo(expected));
                Assert.That(ForcedNumberConversion.Value, Is.EqualTo(NumberConversion.Raw), "serialization must restore the caller's format");
            }
        }
        finally
        {
            ForcedNumberConversion.Value = previousValue;
        }
    }

    [Test]
    public void SetOperationStack_WhenStorageIsRepeated_KeepsFirstSnapshot([Values] bool disableStorage)
    {
        StorageReadCounter reads = new();
        using IContainer container = CreateStorageReadContainer(reads);
        using ILifetimeScope processingScope = CreateStorageProcessingScope(container);
        IWorldState state = processingScope.Resolve<IWorldState>();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        StorageCell cell = SeedStorageSnapshot(state, 5);
        using NativePrestateTracer tracer = new(state,
            GetGethTraceOptions(JsonSerializer.Serialize(new { disableStorage })), Hash256.Zero, TestItem.AddressA, TestItem.AddressB);
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            null!, TestItem.AddressB, TestItem.AddressA, null, 0, UInt256.Zero, default);
        byte[] stack = cell.Index.ToBigEndian();

        tracer.StartOperation(0, Instruction.SLOAD, 100, environment);
        tracer.SetOperationStack(new TraceStack(stack));
        state.Set(cell, (UInt256)9);
        tracer.StartOperation(1, Instruction.SSTORE, 100, environment);
        tracer.SetOperationStack(new TraceStack(stack));

        NativePrestateTracerAccount account =
            ((Dictionary<AddressAsKey, NativePrestateTracerAccount>)tracer.BuildResult().CustomTracerResult!.Value!)[TestItem.AddressB];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reads.Count, Is.EqualTo(disableStorage ? 0 : 1), "repeated slots must not read state again");
            if (disableStorage)
                Assert.That(account.Storage, Is.Null, "disabled storage capture must not create slot entries");
            else
                Assert.That(account.Storage, Is.EquivalentTo(new Dictionary<UInt256, UInt256> { [7] = 5 }),
                    "the prestate must keep the value from before the first access");
        }
    }

    [Test]
    public void SetOperationStack_WhenStorageReadThrows_RemovesPendingSnapshotAndAllowsRetry()
    {
        StorageReadCounter reads = new() { FailNextRead = true };
        using IContainer container = CreateStorageReadContainer(reads);
        using ILifetimeScope processingScope = CreateStorageProcessingScope(container);
        IWorldState state = processingScope.Resolve<IWorldState>();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        StorageCell cell = SeedStorageSnapshot(state, 5);
        using NativePrestateTracer tracer = new(state, GetGethTraceOptions(), Hash256.Zero, TestItem.AddressA, TestItem.AddressB);
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            null!, TestItem.AddressB, TestItem.AddressA, null, 0, UInt256.Zero, default);
        byte[] stack = cell.Index.ToBigEndian();
        tracer.StartOperation(0, Instruction.SLOAD, 100, environment);

        Assert.Throws<InvalidOperationException>(() => tracer.SetOperationStack(new TraceStack(stack)));

        NativePrestateTracerAccount account =
            ((Dictionary<AddressAsKey, NativePrestateTracerAccount>)tracer.BuildResult().CustomTracerResult!.Value!)[TestItem.AddressB];
        Assert.That(account.Storage, Is.Empty, "a failed read must not appear as a zero-valued slot");

        tracer.SetOperationStack(new TraceStack(stack));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reads.Count, Is.EqualTo(2), "the failed slot must be read again on retry");
            Assert.That(account.Storage, Is.EquivalentTo(new Dictionary<UInt256, UInt256> { [7] = 5 }),
                "retry must capture the real storage value");
        }
    }

    [Test]
    public void SetOperationStack_WhenAccountReadThrows_RemovesPendingSnapshotAndAllowsRetry()
    {
        StorageReadCounter reads = new();
        using IContainer container = CreateStorageReadContainer(reads);
        using ILifetimeScope processingScope = CreateStorageProcessingScope(container);
        IWorldState state = processingScope.Resolve<IWorldState>();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(TestItem.AddressC, 9);
        using NativePrestateTracer tracer = new(state, GetGethTraceOptions(), Hash256.Zero, TestItem.AddressA, TestItem.AddressB);
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            null!, TestItem.AddressB, TestItem.AddressA, null, 0, UInt256.Zero, default);
        byte[] stack = TestItem.AddressC.Bytes.PadLeft(32);
        tracer.StartOperation(0, Instruction.BALANCE, 100, environment);
        reads.FailNextAccountRead = true;

        Assert.Throws<InvalidOperationException>(() => tracer.SetOperationStack(new TraceStack(stack)));

        Dictionary<AddressAsKey, NativePrestateTracerAccount> prestate =
            (Dictionary<AddressAsKey, NativePrestateTracerAccount>)tracer.BuildResult().CustomTracerResult!.Value!;
        Assert.That(prestate.ContainsKey(TestItem.AddressC), Is.False, "a failed read must not leave an empty account");

        tracer.SetOperationStack(new TraceStack(stack));

        Assert.That(prestate[TestItem.AddressC].Balance, Is.EqualTo((UInt256)9), "retry must capture the real account");
    }

    private static IContainer CreateStorageReadContainer(StorageReadCounter reads) => new ContainerBuilder()
        .AddModule(new TestNethermindModule(Cancun.Instance))
        .AddSingleton(reads)
        .AddDecorator<IWorldState, RecordingStorageWorldState>()
        .Build();

    private static ILifetimeScope CreateStorageProcessingScope(IContainer container) => container.BeginLifetimeScope(builder =>
        builder.AddSingleton<IWorldStateScopeProvider>(container.Resolve<IWorldStateManager>().GlobalWorldState));

    private static StorageCell SeedStorageSnapshot(IWorldState state, UInt256 value)
    {
        StorageCell cell = new(TestItem.AddressB, 7);
        state.CreateAccount(cell.Address, UInt256.One);
        state.Set(cell, value);
        return cell;
    }

    private static IEnumerable<TestCaseData> AuthoritySignatureCases()
    {
        yield return new TestCaseData(UInt256.One, UInt256.One, 0, true, true);
        yield return new TestCaseData(UInt256.One, SecP256k1Curve.HalfN, 1, true, true);
        yield return new TestCaseData(UInt256.Zero, UInt256.One, 0, true, false);
        yield return new TestCaseData(SecP256k1Curve.N, UInt256.One, 0, true, false);
        yield return new TestCaseData(UInt256.One, UInt256.Zero, 0, true, false);
        yield return new TestCaseData(UInt256.One, SecP256k1Curve.HalfNPlusOne, 0, true, false);
        yield return new TestCaseData(UInt256.One, UInt256.One, 2, true, false);
        yield return new TestCaseData(UInt256.One, UInt256.One, 0, false, false);
    }

    [TestCaseSource(nameof(AuthoritySignatureCases))]
    public void Prestate_captures_only_signature_valid_recovered_authorities(UInt256 r, UInt256 s, int parity, bool recovered, bool captured)
    {
        TestState.CreateAccount(TestItem.AddressD, UInt256.One);
        AuthorizationTuple authorization = new(9999, TestItem.AddressE, ulong.MaxValue, (byte)parity, r, s,
            recovered ? TestItem.AddressD : null);
        Transaction transaction = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithTo(TestItem.AddressB)
            .WithAuthorizationCode(authorization).TestObject;
        Block block = Build.A.Block.WithBeneficiary(TestItem.AddressC).TestObject;
        using GethLikeNativeTxTracer tracer = GethLikeNativeTracerFactory.CreateTracer(
            GetGethTraceOptions(), block, transaction, TestState, Prague.Instance);
        Dictionary<AddressAsKey, NativePrestateTracerAccount> accounts =
            (Dictionary<AddressAsKey, NativePrestateTracerAccount>)tracer.BuildResult().CustomTracerResult!.Value!;
        Assert.That(accounts.ContainsKey(TestItem.AddressD), Is.EqualTo(captured));
    }

    [Test]
    public void Prestate_filters_empty_authorities_using_configuration([Values] bool includeEmpty, [Values] bool disableCode)
    {
        TestState.CreateAccount(TestItem.AddressD, UInt256.Zero);
        AuthorizationTuple authorization = new(0, TestItem.AddressE, 0, 0, UInt256.One, UInt256.One, TestItem.AddressD);
        Transaction transaction = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithTo(TestItem.AddressB)
            .WithAuthorizationCode(authorization).TestObject;
        Block block = Build.A.Block.WithBeneficiary(TestItem.AddressC).TestObject;
        using GethLikeNativeTxTracer tracer = GethLikeNativeTracerFactory.CreateTracer(
            GetGethTraceOptions(JsonSerializer.Serialize(new { includeEmpty, disableCode })), block, transaction, TestState, Prague.Instance);
        Dictionary<AddressAsKey, NativePrestateTracerAccount> accounts =
            (Dictionary<AddressAsKey, NativePrestateTracerAccount>)tracer.BuildResult().CustomTracerResult!.Value!;
        Assert.That(accounts.ContainsKey(TestItem.AddressD), Is.EqualTo(includeEmpty));
    }

    [Test]
    public void Execute_WhenPrestateIsFiltered_ObservesOnlyRequiredOpcodes([Values] bool fullTrace, [Values] bool disableStorage)
    {
        RecordingPrestateTracer tracer = new(TestState, disableStorage);
        using ITxTracer executionTracer = new CancellationTxTracer(new CompositeTxTracer(
            tracer, fullTrace ? new FullInstructionTracer() : NullTxTracer.Instance));
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .PushData(2)
            .Op(Instruction.ADD)
            .PushData(0)
            .Op(Instruction.SLOAD)
            .PushData(7)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .PushData(TestItem.AddressC)
            .Op(Instruction.BALANCE)
            .Op(Instruction.STOP)
            .Done;

        Execute(executionTracer, code, MainnetSpecProvider.CancunActivation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Operations.Contains(Instruction.SLOAD), Is.EqualTo(fullTrace || !disableStorage));
            Assert.That(tracer.Operations.Contains(Instruction.SSTORE), Is.EqualTo(fullTrace || !disableStorage));
            Assert.That(tracer.Operations, Does.Contain(Instruction.BALANCE), "account reads must remain selected when storage capture is disabled");
            Assert.That(tracer.Operations.Contains(Instruction.ADD), Is.EqualTo(fullTrace));
            Assert.That(tracer.Error, Is.Null);
        }
    }

    [Test]
    public void Execute_WhenOpcodeFails_ReportsTheError(
        [Values(Instruction.ADD, Instruction.SLOAD, Instruction.SSTORE)] Instruction opcode,
        [Values] bool disableStorage)
    {
        using RecordingPrestateTracer tracer = new(TestState, disableStorage);

        Execute(tracer, Prepare.EvmCode.Op(opcode).Done, MainnetSpecProvider.CancunActivation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Operations.Contains(opcode), Is.EqualTo(opcode != Instruction.ADD && !disableStorage));
            Assert.That(tracer.Error, Is.EqualTo(EvmExceptionType.StackUnderflow));
        }
    }

    [Test]
    public void BuildResult_FiltersInitiallyEmptyAccounts([Values] bool includeEmpty, [Values] bool disableCode)
    {
        TestState.CreateAccount(TestItem.AddressD, UInt256.Zero);
        TestState.InsertCode(TestItem.AddressD, new byte[] { 0x00 }, Spec);
        string config = $$"""{"includeEmpty":{{includeEmpty.ToString().ToLowerInvariant()}},"disableCode":{{disableCode.ToString().ToLowerInvariant()}}}""";
        using NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(config), Hash256.Zero,
            TestItem.AddressC, TestItem.AddressD, TestItem.AddressE);

        GethLikeTxTrace result = tracer.BuildResult();
        Dictionary<AddressAsKey, NativePrestateTracerAccount> accounts =
            (Dictionary<AddressAsKey, NativePrestateTracerAccount>)result.CustomTracerResult!.Value!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accounts.ContainsKey(TestItem.AddressC), Is.EqualTo(includeEmpty));
            Assert.That(accounts.ContainsKey(TestItem.AddressE), Is.EqualTo(includeEmpty));
            Assert.That(accounts.ContainsKey(TestItem.AddressD), Is.True, "Code-only accounts must remain even with disableCode");
        }
    }

    [Test]
    public void Prestate_filters_initially_empty_creation_with_storage([Values] bool includeEmpty, [Values] bool disableStorage)
    {
        (Block block, Transaction transaction) = PrepareTx(MainnetSpecProvider.CancunActivation, 500000, value: 0);
        transaction.To = null;
        transaction.Data = Bytes.FromHexString("600160005500");
        Address created = ContractAddress.From(transaction.SenderAddress!, transaction.Nonce);
        Assert.That(TestState.AccountExists(created), Is.False);
        IReleaseSpec spec = SpecProvider.GetSpec(block.Header);
        using GethLikeNativeTxTracer tracer = GethLikeNativeTracerFactory.CreateTracer(
            GetGethTraceOptions(JsonSerializer.Serialize(new { includeEmpty, disableStorage })), block, transaction, TestState, spec);

        _processor.Execute(transaction, new BlockExecutionContext(block.Header, spec), tracer);
        Dictionary<AddressAsKey, NativePrestateTracerAccount> accounts =
            (Dictionary<AddressAsKey, NativePrestateTracerAccount>)tracer.BuildResult().CustomTracerResult!.Value!;

        using (Assert.EnterMultipleScope())
        {
            TestState.Get(new StorageCell(created, 0), out UInt256 stored);
            Assert.That(stored, Is.EqualTo(UInt256.One), "initcode must execute SSTORE");
            Assert.That(accounts.ContainsKey(created), Is.EqualTo(includeEmpty));
        }
        if (includeEmpty)
            Assert.That(accounts[created].Storage?.Count ?? 0, Is.EqualTo(disableStorage ? 0 : 1));
    }

    [Test]
    public void DiffMode_OmitsInitiallyEmptySenderFromPrestate()
    {
        using NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(DiffMode), Hash256.Zero,
            TestItem.AddressD, TestItem.AddressE);
        TestState.CreateAccount(TestItem.AddressD, UInt256.Zero);
        TestState.IncrementNonce(TestItem.AddressD);
        tracer.MarkAsSuccess(TestItem.AddressE, default, [], []);

        NativePrestateTracerDiffMode result = (NativePrestateTracerDiffMode)tracer.BuildResult().CustomTracerResult!.Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.pre.ContainsKey(TestItem.AddressD), Is.False);
            Assert.That(result.post[TestItem.AddressD].Nonce, Is.EqualTo((UInt256)1));
        }
    }

    [Test]
    public void Constructor_RejectsDiffModeWithIncludeEmpty() =>
        Assert.That(() => new NativePrestateTracer(TestState,
            GetGethTraceOptions("""{"diffMode":true,"includeEmpty":true}"""), Hash256.Zero, TestItem.AddressA),
            Throws.ArgumentException.With.Message.EqualTo("cannot use diffMode with includeEmpty"));

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

    /// <summary>As in geth, an operation error ends only the failing operation: the caller that survives it is
    /// still captured, opcode by opcode, without the failing operation's capture flags leaking into it.</summary>
    [Test]
    public void SetOperationStack_AfterAnOperationHasErrored_KeepsCapturing([Values] bool errored)
    {
        using NativePrestateTracer tracer = new(TestState, GetGethTraceOptions("""{"includeEmpty":true}"""),
            Hash256.Zero, TestItem.AddressA, TestItem.AddressB);
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            null!, TestItem.AddressB, TestItem.AddressA, null, 0, UInt256.Zero, default);

        tracer.StartOperation(0, Instruction.SLOAD, 100, environment);
        if (errored)
        {
            tracer.ReportOperationError(EvmExceptionType.OutOfGas);
        }

        // The frame that halted unwinds, but its caller keeps executing: an unrelated opcode captures nothing
        tracer.StartOperation(1, Instruction.ADD, 100, environment);
        bool tracingStackOnAdd = tracer.IsTracingStack;

        // while a later SLOAD of the caller is still captured
        tracer.StartOperation(2, Instruction.SLOAD, 100, environment);
        bool tracingStackOnSLoad = tracer.IsTracingStack;
        tracer.SetOperationStack(new TraceStack(new byte[EvmStack.WordSize]));

        NativePrestateTracerAccount account =
            ((Dictionary<AddressAsKey, NativePrestateTracerAccount>)tracer.BuildResult().CustomTracerResult!.Value)[TestItem.AddressB];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracingStackOnAdd, Is.False);
            Assert.That(tracingStackOnSLoad, Is.True);
            Assert.That(account.Storage?.Count ?? 0, Is.EqualTo(1));
        }
    }

    [Test]
    public void Prestate_captures_parent_accesses_after_child_exit([Values("00", "fe", "60006000fd")] string childCode)
    {
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, Bytes.FromHexString(childCode), Spec);
        TestState.CreateAccount(TestItem.AddressD, 1.Ether);
        TestState.Set(new StorageCell(TestItem.AddressB, 0), (UInt256)42);
        byte[] code = Prepare.EvmCode
            .Call(TestItem.AddressC, 50000)
            .Op(Instruction.POP)
            .PushData(TestItem.AddressD)
            .Op(Instruction.BALANCE)
            .Op(Instruction.POP)
            .PushData(0)
            .Op(Instruction.SLOAD)
            .Op(Instruction.STOP)
            .Done;
        using NativePrestateTracer tracer = new(TestState, GetGethTraceOptions("""{"includeEmpty":true}"""),
            Hash256.Zero, TestItem.AddressA, TestItem.AddressB);

        Execute(tracer, code, MainnetSpecProvider.CancunActivation);
        Dictionary<AddressAsKey, NativePrestateTracerAccount> accounts =
            (Dictionary<AddressAsKey, NativePrestateTracerAccount>)tracer.BuildResult().CustomTracerResult!.Value!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accounts.ContainsKey(TestItem.AddressD), Is.True);
            Assert.That(accounts[TestItem.AddressB].Storage![UInt256.Zero], Is.EqualTo((UInt256)42));
        }
    }

    [Test]
    public void Prestate_serializes_nonempty_code_hash(
        [Values(null, "", "00", "ef01000000000000000000000000000000000000001234")] string? code,
        [Values] bool disableCode)
    {
        if (code is not null)
        {
            TestState.CreateAccount(TestItem.AddressB, 1);
            TestState.InsertCode(TestItem.AddressB, Bytes.FromHexString(code), Spec);
        }
        using NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(JsonSerializer.Serialize(new { disableCode, includeEmpty = true })),
            Hash256.Zero, TestItem.AddressA, TestItem.AddressB);

        JsonElement result = JsonSerializer.SerializeToElement(tracer.BuildResult().CustomTracerResult!.Value, SerializerOptions);
        JsonElement account = result.GetProperty(TestItem.AddressB.ToString());
        bool hasCode = !string.IsNullOrEmpty(code);
        bool hasHash = account.TryGetProperty("codeHash", out JsonElement codeHash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.TryGetProperty("code", out _), Is.EqualTo(hasCode && !disableCode));
            Assert.That(hasHash, Is.EqualTo(hasCode));
            if (hasCode && hasHash)
                Assert.That(codeHash.GetString(), Is.EqualTo(Keccak.Compute(Bytes.FromHexString(code!)).ToString()));
        }
    }

    private static IEnumerable<TestCaseData> CodeChangeCases()
    {
        (string Before, string? After)[] cases =
        [
            ("00", "00"), ("00", "6000"), ("", "00"), ("00", ""), ("", ""),
            ("00", null), ("", null), ("ef01000000000000000000000000000000000000001234", "")
        ];
        foreach ((string before, string? after) in cases)
            foreach (bool disableCode in new[] { false, true })
                yield return new TestCaseData(before, after, disableCode);
    }

    [TestCaseSource(nameof(CodeChangeCases))]
    public void Diff_serializes_changed_code_and_hash(string before, string? after, bool disableCode)
    {
        byte[] beforeCode = Bytes.FromHexString(before);
        TestState.CreateAccount(TestItem.AddressB, 1);
        TestState.InsertCode(TestItem.AddressB, beforeCode, Spec);
        using NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(JsonSerializer.Serialize(new { diffMode = true, disableCode })),
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
            Assert.That(pre.TryGetProperty("code", out _), Is.EqualTo(before.Length > 0 && !disableCode));
            Assert.That(pre.TryGetProperty("codeHash", out JsonElement preHash), Is.EqualTo(before.Length > 0));
            if (before.Length > 0)
                Assert.That(preHash.GetString(), Is.EqualTo(Keccak.Compute(beforeCode).ToString()));
            Assert.That(post.TryGetProperty("codeHash", out JsonElement postHash), Is.EqualTo(before != after));
            if (before != after)
                Assert.That(postHash.GetString(), Is.EqualTo((after is null ? Hash256.Zero : Keccak.Compute(Bytes.FromHexString(after))).ToString()));
            bool codeChanged = !disableCode && before != (after ?? "");
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

    [TestCase("""{"includeEmpty":true}""", ExpectedSStorePrestateTrace, false)]
    [TestCase("""{"diffMode":false,"includeEmpty":true}""", ExpectedSStorePrestateTrace, false)]
    [TestCase(DiffMode, ExpectedSStoreDiffModeTrace, false)]
    [TestCase("""{"includeEmpty":true}""", ExpectedSStorePrestateTrace, true)]
    [TestCase("""{"diffMode":false,"includeEmpty":true}""", ExpectedSStorePrestateTrace, true)]
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
          "0x0000000000000000000000000000000000000000": {
            "balance": "0x56bc75e2d63100000"
          },
          "0x76e68a8696537e4141926f3e528733af9e237d69": {
            "balance": "0xde0b6b3a7640000",
            "code": "0x7f7f000000000000000000000000000000000000000000000000000000000000006000527f0060005260036000f30000000000000000000000000000000000000000000000602052602960006000f000",
            "codeHash": "0xed25a1b0948283ea4844073b1cdb8e1bc6624420c646ad850059b2b8086faaa9"
          }
        }
        """;

    private const string ExpectedNestedCallsDiffModeTrace = """
        {
          "pre": {},
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
          "0x0000000000000000000000000000000000000000": {
            "balance": "0x56bc75e2d63100000"
          },
          "0x76e68a8696537e4141926f3e528733af9e237d69": {
            "balance": "0xde0b6b3a7640000",
            "code": "0x7f7f010203000000000000000000000000000000000000000000000000000000006000527f0060005260036000f3000000000000000000000000000000000000000000000060205262040506602960006000f5",
            "codeHash": "0xb2139a4067bf64811bda94f4d73a4a9fdb7d5015e57a92274e9e009e48354dc5"
          }
        }
        """;

    private const string ExpectedCreate2DiffModeTrace = """
        {
          "pre": {
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

    public enum CreateTargetState
    {
        Absent,
        Funded,
        StorageOnly,
        FundedWithStorage
    }

    /// <summary>A CREATE or CREATE2 that lands on an existing account (a balance or storage alone does not collide
    /// under EIP-684) keeps that account in diffMode's pre when it held a balance, while an absent or empty target,
    /// where emptiness ignores storage, is dropped.</summary>
    [Test]
    public void Test_PrestateTrace_CreateTarget_IsKeptInPreOnlyWhenNonEmpty(
        [Values(Instruction.CREATE, Instruction.CREATE2)] Instruction opcode, [Values] CreateTargetState targetState)
    {
        byte[] salt = { 4, 5, 6 };
        byte[] initCode = Prepare.EvmCode.ForInitOf([1, 2, 3]).Done;
        Address created = opcode == Instruction.CREATE
            ? ContractAddress.From(TestItem.AddressC, 0)
            : ContractAddress.From(TestItem.AddressC, salt.PadLeft(32), initCode);
        byte[] createCode = opcode == Instruction.CREATE
            ? Prepare.EvmCode.Create(initCode, 0).Done
            : Prepare.EvmCode.Create2(initCode, salt, 0).Done;

        TestState.CreateAccount(Address.Zero, 100.Ether);
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, createCode, Spec);
        UInt256 balance = targetState is CreateTargetState.Funded or CreateTargetState.FundedWithStorage ? 7u : 0u;
        if (targetState != CreateTargetState.Absent) TestState.CreateAccount(created, balance);
        if (targetState is CreateTargetState.StorageOnly or CreateTargetState.FundedWithStorage) TestState.Set(new StorageCell(created, 1), 5);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(DiffMode), Hash256.Zero, TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace trace = ExecutePrestate(tracer, Prepare.EvmCode.Call(TestItem.AddressC, 50000).Done, wrapped: false);

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(trace.CustomTracerResult?.Value, SerializerOptions));
        JsonElement pre = document.RootElement.GetProperty("pre");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.RootElement.GetProperty("post").GetProperty(created.ToString()).GetProperty("code").GetString(),
                Is.EqualTo("0x010203"), "the creation must not have collided");
            Assert.That(pre.TryGetProperty(created.ToString(), out JsonElement account) ? account.GetProperty("balance").GetString() : null,
                Is.EqualTo(balance.IsZero ? null : balance.ToHexString(true)));
        }
    }

    [Test]
    public void Test_PrestateTrace_CreateCollisionAfterValueTransfer_PreservesCodeBearingPrestate(
        [Values(Instruction.CREATE, Instruction.CREATE2)] Instruction opcode, [Values] bool disableCode)
    {
        byte[] salt = { 4, 5, 6 };
        byte[] initCode = Prepare.EvmCode.ForInitOf([1, 2, 3]).Done;
        Address created = opcode == Instruction.CREATE
            ? ContractAddress.From(TestItem.AddressC, 0)
            : ContractAddress.From(TestItem.AddressC, salt.PadLeft(32), initCode);
        Prepare creator = Prepare.EvmCode
            .PushData(0).PushData(0).PushData(0).PushData(0)
            .PushData(1).PushData(created).PushData(50_000)
            .Op(Instruction.CALL).Op(Instruction.POP);
        byte[] creatorCode = opcode == Instruction.CREATE
            ? creator.Create(initCode, 0).Done
            : creator.Create2(initCode, salt, 0).Done;
        byte[] existingCode = [(byte)Instruction.STOP];

        TestState.CreateAccount(Address.Zero, 100.Ether);
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, creatorCode, Spec);
        TestState.CreateAccount(created, UInt256.Zero);
        TestState.InsertCode(created, existingCode, Spec);

        string config = JsonSerializer.Serialize(new { diffMode = true, disableCode });
        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(config), Hash256.Zero,
            TestItem.AddressA, TestItem.AddressB, Address.Zero);
        GethLikeTxTrace trace = ExecutePrestate(tracer, Prepare.EvmCode.Call(TestItem.AddressC, 50000).Done, wrapped: false);

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(trace.CustomTracerResult?.Value, SerializerOptions));
        JsonElement prestate = document.RootElement.GetProperty("pre").GetProperty(created.ToString());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(prestate.GetProperty("balance").GetString(), Is.EqualTo("0x0"));
            Assert.That(prestate.GetProperty("codeHash").GetString(), Is.EqualTo(Keccak.Compute(existingCode).ToString()));
            Assert.That(prestate.TryGetProperty("code", out JsonElement code), Is.EqualTo(!disableCode));
            if (!disableCode) Assert.That(code.GetString(), Is.EqualTo("0x00"));
            Assert.That(document.RootElement.GetProperty("post").GetProperty(created.ToString()).GetProperty("balance").GetString(),
                Is.EqualTo("0x1"));
        }
    }

    public enum ChildOutcome
    {
        Reverted,
        OutOfGas,
        InvalidOpcode,
        StackUnderflow,
        NotEnoughBalance
    }

    private static readonly Address ProbedAccount = new("0xbabababababababababababababababababababa");
    private static readonly UInt256 CallerBalance = 10;
    private static readonly UInt256 PayeeBalance = 1;
    private static readonly UInt256 ProbedBalance = 3;

    /// <summary>As in geth, whose prestate tracer skips only the opcode that fails, a child frame that halts
    /// exceptionally (or a CALL that fails its balance pre-check) leaves the rest of the caller traced: the accounts
    /// and slots it touches afterwards show in pre and, when they change, in post.</summary>
    [Test]
    public void Test_PrestateTrace_AfterAbortedChild_TracesTheCallerToTheEnd([Values] ChildOutcome outcome, [Values] bool diffMode, [Values] bool wrapped)
    {
        Address child = TestItem.AddressF;
        Address payee = TestItem.AddressE;
        Address caller = TestItem.AddressC;
        byte[] childCode = outcome switch
        {
            ChildOutcome.Reverted => Prepare.EvmCode.Revert(0, 0).Done,
            ChildOutcome.OutOfGas => Prepare.EvmCode.Op(Instruction.JUMPDEST).PushData(0).Op(Instruction.JUMP).Done,
            ChildOutcome.InvalidOpcode => Prepare.EvmCode.Op(Instruction.INVALID).Done,
            ChildOutcome.StackUnderflow => Prepare.EvmCode.Op(Instruction.ADD).Done,
            _ => Prepare.EvmCode.Op(Instruction.STOP).Done,
        };
        Prepare callerCode = outcome == ChildOutcome.NotEnoughBalance
            ? Prepare.EvmCode.CallWithValue(child, 10_000, CallerBalance + 1)
            : Prepare.EvmCode.Call(child, 10_000);
        callerCode = callerCode.Op(Instruction.POP)
            .CallWithValue(payee, 0, 1).Op(Instruction.POP)
            .PushData(ProbedAccount).Op(Instruction.BALANCE).Op(Instruction.POP)
            .PersistData("0x01", "0x07")
            .Op(Instruction.STOP);

        TestState.CreateAccount(Address.Zero, 100.Ether);
        TestState.CreateAccount(caller, CallerBalance);
        TestState.InsertCode(caller, callerCode.Done, Spec);
        TestState.Set(new StorageCell(caller, 1), 5);
        TestState.CreateAccount(child, UInt256.Zero);
        TestState.InsertCode(child, childCode, Spec);
        TestState.CreateAccount(payee, PayeeBalance);
        TestState.CreateAccount(ProbedAccount, ProbedBalance);

        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(diffMode ? DiffMode : PrestateMode), Hash256.Zero,
            TestItem.AddressA, TestItem.AddressB, Address.Zero);
        object result = ExecutePrestate(tracer, Prepare.EvmCode.Call(caller, 80_000).Done, wrapped).CustomTracerResult!.Value;

        using (Assert.EnterMultipleScope())
        {
            if (diffMode)
            {
                NativePrestateTracerDiffMode diff = (NativePrestateTracerDiffMode)result;
                Assert.That(diff.pre[payee].Balance, Is.EqualTo((UInt256?)PayeeBalance));
                Assert.That(diff.post[payee].Balance, Is.EqualTo((UInt256?)(PayeeBalance + 1)));
                Assert.That(diff.pre[caller].Balance, Is.EqualTo((UInt256?)CallerBalance));
                Assert.That(diff.pre[caller].Storage![1], Is.EqualTo((UInt256)5));
                Assert.That(diff.post[caller].Balance, Is.EqualTo((UInt256?)(CallerBalance - 1)));
                Assert.That(diff.post[caller].Storage![1], Is.EqualTo((UInt256)7));
            }
            else
            {
                Dictionary<AddressAsKey, NativePrestateTracerAccount> pre = (Dictionary<AddressAsKey, NativePrestateTracerAccount>)result;
                Assert.That(pre.ContainsKey(child), Is.True);
                Assert.That(pre[payee].Balance, Is.EqualTo((UInt256?)PayeeBalance));
                Assert.That(pre[ProbedAccount].Balance, Is.EqualTo((UInt256?)ProbedBalance));
                Assert.That(pre[caller].Storage![1], Is.EqualTo((UInt256)5));
            }
        }
    }

    private const string ExpectedExistingAccountPrestateTrace = """
        {
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
          "pre": {},
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
          "0x0000000000000000000000000000000000000000": {
            "balance": "0x56bc75e2d63100000"
          }
        }
        """;

    private const string ExpectedEmptyToDiffModeTrace = """
        {
          "pre": {},
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
          "pre": {},
          "post": {
            "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
              "balance": "0x56bc75e2d630f2e9c",
              "nonce": 1
            },
            "0x24cd2edba056b7c654a50e8201b619d4f624fdda": {
              "codeHash": "0x0000000000000000000000000000000000000000000000000000000000000000"
            },
            "0x76e68a8696537e4141926f3e528733af9e237d69": {
              "balance": "0x56bc75e2d63100001"
            }
          }
        }
        """;

    [TestCase(NoConfig, ExpectedEmptyToPrestateTrace)]
    [TestCase("""{"includeEmpty":true}""", ExpectedSelfDestructPrestateTrace)]
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

    private static readonly UInt256 VictimBalance = 7;
    private static readonly UInt256 HeirBalance = 1;

    /// <summary>As in geth, the account executing SELFDESTRUCT is marked deleted when the opcode runs, even if the
    /// transaction reverts: it stays whole in pre and is left out of post. After EIP-6780 a pre-existing account
    /// survives, so it is a regular diff.</summary>
    [Test]
    public void Test_PrestateTrace_SelfDestruct_OfExistingAccount_DeletesItOnlyBeforeCancun([Values] bool cancun, [Values] bool toItself, [Values] bool revert)
    {
        bool deleted = !cancun;
        bool changed = !toItself && !revert;
        byte[] victimCode = toItself
            ? Prepare.EvmCode.Op(Instruction.ADDRESS).Op(Instruction.SELFDESTRUCT).Done
            : Prepare.EvmCode.SELFDESTRUCT(TestItem.AddressE).Done;
        NativePrestateTracerDiffMode diff = TraceSelfDestruct(cancun, victimCode, revert);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.pre.TryGetValue(TestItem.AddressC, out NativePrestateTracerAccount? pre), Is.EqualTo(deleted || changed));
            Assert.That(pre?.Balance, Is.EqualTo(deleted || changed ? (UInt256?)VictimBalance : null));
            Assert.That(diff.post.TryGetValue(TestItem.AddressC, out NativePrestateTracerAccount? post), Is.EqualTo(!deleted && changed));
            Assert.That(post?.Balance, Is.EqualTo(!deleted && changed ? (UInt256?)UInt256.Zero : null));
        }
    }

    /// <summary>Port of geth's <c>prestate_tracer_with_diff_mode/suicide_cancun.json</c>: a pre-existing contract
    /// that writes storage and self-destructs under Cancun stays in post with a zero balance.</summary>
    [Test]
    public void Test_PrestateTrace_SelfDestruct_SuicideCancun()
    {
        Address victim = new("0x2222222222222222222222222222222222222222");
        Address heir = new("0x1111111111111111111111111111111111111111");
        byte[] victimCode = Bytes.FromHexString("0x6099600155731111111111111111111111111111111111111111ff");
        TestState.CreateAccount(Address.Zero, 100.Ether);
        TestState.CreateAccount(heir, 0);
        TestState.CreateAccount(victim, 1.Ether, 1);
        TestState.InsertCode(victim, victimCode, Spec);
        TestState.Set(new StorageCell(victim, 1), (UInt256)0xabcd);
        TestState.Set(new StorageCell(victim, 2), (UInt256)0x1234);

        IReleaseSpec spec = SpecProvider.GetSpec(MainnetSpecProvider.CancunActivation);
        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(DiffMode), Hash256.Zero, TestItem.AddressA, TestItem.AddressB, Address.Zero, releaseSpec: spec);
        Execute(tracer, Prepare.EvmCode.Call(victim, 50_000).Done, MainnetSpecProvider.CancunActivation);
        NativePrestateTracerDiffMode diff = (NativePrestateTracerDiffMode)tracer.BuildResult().CustomTracerResult!.Value;

        using (Assert.EnterMultipleScope())
        {
            NativePrestateTracerAccount pre = diff.pre[victim];
            Assert.That(pre.Balance, Is.EqualTo((UInt256?)1.Ether));
            Assert.That(pre.Nonce, Is.EqualTo((UInt256?)1));
            Assert.That(pre.Code.ToArray(), Is.EqualTo(victimCode));
            Assert.That(pre.Storage, Is.EquivalentTo(new Dictionary<UInt256, UInt256> { [1] = 0xabcd }));

            NativePrestateTracerAccount post = diff.post[victim];
            Assert.That(post.Balance, Is.EqualTo((UInt256?)UInt256.Zero));
            Assert.That(post.Nonce, Is.Null);
            Assert.That(post.Code.IsEmpty, Is.True);
            Assert.That(post.Storage, Is.EquivalentTo(new Dictionary<UInt256, UInt256> { [1] = 0x99 }));
            Assert.That(diff.post[heir].Balance, Is.EqualTo((UInt256?)1.Ether));
        }
    }

    [Test]
    public void Test_PrestateTrace_SelfDestruct_BeneficiaryIsABalanceChange([Values] bool cancun)
    {
        NativePrestateTracerDiffMode diff = TraceSelfDestruct(cancun, Prepare.EvmCode.SELFDESTRUCT(TestItem.AddressE).Done);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.pre[TestItem.AddressE].Balance, Is.EqualTo((UInt256?)HeirBalance));
            Assert.That(diff.post[TestItem.AddressE].Balance, Is.EqualTo((UInt256?)(HeirBalance + VictimBalance)));
        }
    }

    /// <summary>An account created and self-destructed in one transaction is deleted under every fork; it shows in
    /// pre only when it held a balance before the transaction.</summary>
    [Test]
    public void Test_PrestateTrace_SelfDestruct_OfSameTxCreation_DeletesIt([Values] bool cancun, [Values] bool prefunded)
    {
        const int endowment = 3;
        UInt256 prefund = prefunded ? 2u : 0u;
        byte[] initCode = Prepare.EvmCode.SELFDESTRUCT(TestItem.AddressE).Done;
        Address created = ContractAddress.From(TestItem.AddressC, 0);
        if (prefunded) TestState.CreateAccount(created, prefund);

        NativePrestateTracerDiffMode diff = TraceSelfDestruct(cancun, Prepare.EvmCode.Create(initCode, endowment).Done);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.pre.TryGetValue(created, out NativePrestateTracerAccount? pre), Is.EqualTo(prefunded));
            if (prefunded) Assert.That(pre!.Balance, Is.EqualTo((UInt256?)prefund));
            Assert.That(diff.post.ContainsKey(created), Is.False);
            Assert.That(diff.post[TestItem.AddressE].Balance, Is.EqualTo((UInt256?)(HeirBalance + endowment + prefund)));
        }
    }

    /// <summary>As in geth, the contract of a contract-creation transaction counts as created in it, so initcode
    /// that self-destructs deletes it under every fork.</summary>
    [Test]
    public void Test_PrestateTrace_SelfDestruct_InCreationTx_DeletesTheCreatedContract([Values] bool cancun)
    {
        const int prefund = 2;
        ForkActivation fork = cancun ? MainnetSpecProvider.CancunActivation : MainnetSpecProvider.ShanghaiActivation;
        TestState.CreateAccount(TestItem.AddressE, HeirBalance);
        (Block block, Transaction transaction) = PrepareInitTx(fork, 100_000, Prepare.EvmCode.SELFDESTRUCT(TestItem.AddressE).Done);
        Address created = ContractAddress.From(transaction.SenderAddress!, transaction.Nonce);
        TestState.CreateAccount(created, prefund);
        TestState.Commit(SpecProvider.GenesisSpec);

        IReleaseSpec spec = SpecProvider.GetSpec(block.Header);
        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(DiffMode), transaction.Hash, transaction.SenderAddress, null, block.Beneficiary, transaction, spec);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, spec), tracer);
        NativePrestateTracerDiffMode diff = (NativePrestateTracerDiffMode)tracer.BuildResult().CustomTracerResult!.Value;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.pre[created].Balance, Is.EqualTo((UInt256?)(UInt256)prefund));
            Assert.That(diff.post.ContainsKey(created), Is.False);
            Assert.That(diff.post[TestItem.AddressE].Balance, Is.EqualTo((UInt256?)(HeirBalance + prefund + transaction.Value)));
        }
    }

    /// <summary>EIP-8246: a same-transaction creation that self-destructs keeps the balance it ends the transaction
    /// with, whether kept by a SELFDESTRUCT to itself or received afterwards, so it shows in post as a balance-only
    /// account; with no balance EIP-161 removes it and it shows nowhere.</summary>
    [Test]
    public void Test_PrestateTrace_SelfDestruct_UnderEip8246_KeepsTheBalance([Values] bool toItself, [Values] bool funded)
    {
        const int balance = 3;
        Address created = ContractAddress.From(TestItem.AddressC, 0);
        byte[] initCode = toItself
            ? Prepare.EvmCode.Op(Instruction.ADDRESS).Op(Instruction.SELFDESTRUCT).Done
            : Prepare.EvmCode.SELFDESTRUCT(TestItem.AddressE).Done;
        Prepare victimCode = Prepare.EvmCode.Create(initCode, toItself && funded ? balance : 0u);
        if (!toItself && funded) victimCode = victimCode.CallWithValue(created, 50_000, balance);

        NativePrestateTracerDiffMode diff = TraceSelfDestruct(MainnetSpecProvider.AmsterdamActivation, victimCode.Done);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.post[TestItem.AddressC].Nonce, Is.EqualTo((UInt256?)1), "CREATE must succeed");
            Assert.That(diff.pre.ContainsKey(created), Is.False);
            Assert.That(diff.post.TryGetValue(created, out NativePrestateTracerAccount? post), Is.EqualTo(funded));
            if (funded)
            {
                Assert.That(post!.Balance, Is.EqualTo((UInt256?)(UInt256)balance));
                Assert.That(post.Nonce, Is.Null);
                Assert.That(post.Code.IsEmpty, Is.True);
                Assert.That(post.Storage, Is.Null);
            }
        }
    }

    /// <summary>EIP-8246: the contract of a contract-creation transaction whose initcode self-destructs to itself keeps
    /// its balance.</summary>
    [Test]
    public void Test_PrestateTrace_SelfDestruct_InCreationTxUnderEip8246_KeepsTheBalance()
    {
        const int prefund = 2;
        ForkActivation fork = MainnetSpecProvider.AmsterdamActivation;
        // EIP-8037 state gas makes an Amsterdam creation tx cost far more than 100k.
        (Block block, Transaction transaction) = PrepareInitTx(fork, 1_000_000, Prepare.EvmCode.Op(Instruction.ADDRESS).Op(Instruction.SELFDESTRUCT).Done);
        Address created = ContractAddress.From(transaction.SenderAddress!, transaction.Nonce);
        TestState.CreateAccount(created, prefund);
        TestState.Commit(SpecProvider.GenesisSpec);

        IReleaseSpec spec = SpecProvider.GetSpec(block.Header);
        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(DiffMode), transaction.Hash, transaction.SenderAddress, null, block.Beneficiary, transaction, spec);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, spec), tracer);
        NativePrestateTracerDiffMode diff = (NativePrestateTracerDiffMode)tracer.BuildResult().CustomTracerResult!.Value;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.pre[created].Balance, Is.EqualTo((UInt256?)(UInt256)prefund));
            Assert.That(diff.post.TryGetValue(created, out NativePrestateTracerAccount? post), Is.True);
            Assert.That(post?.Balance, Is.EqualTo((UInt256?)(prefund + transaction.Value)));
        }
    }

    /// <summary>Calls <see cref="TestItem.AddressC"/>, which runs <paramref name="victimCode"/>, with
    /// <see cref="TestItem.AddressE"/> as an existing beneficiary.</summary>
    private NativePrestateTracerDiffMode TraceSelfDestruct(bool cancun, byte[] victimCode, bool revert = false) =>
        TraceSelfDestruct(cancun ? MainnetSpecProvider.CancunActivation : MainnetSpecProvider.ShanghaiActivation, victimCode, revert);

    private NativePrestateTracerDiffMode TraceSelfDestruct(ForkActivation fork, byte[] victimCode, bool revert = false)
    {
        TestState.CreateAccount(Address.Zero, 100.Ether);
        TestState.CreateAccount(TestItem.AddressE, HeirBalance);
        TestState.CreateAccount(TestItem.AddressC, VictimBalance);
        TestState.InsertCode(TestItem.AddressC, victimCode, Spec);

        // EIP-8037 state gas makes an Amsterdam CREATE cost far more than 50k.
        Prepare code = Prepare.EvmCode.Call(TestItem.AddressC, 500_000);
        if (revert) code = code.Revert(0, 0);

        (Block block, Transaction transaction) = PrepareTx(fork, 1_000_000, code.Done);
        IReleaseSpec spec = SpecProvider.GetSpec(block.Header);
        NativePrestateTracer tracer = new(TestState, GetGethTraceOptions(DiffMode), Hash256.Zero, TestItem.AddressA, TestItem.AddressB, Address.Zero,
            releaseSpec: spec);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, spec), tracer);
        return (NativePrestateTracerDiffMode)tracer.BuildResult().CustomTracerResult!.Value;
    }

    [Test]
    public void Prestate_captures_delegation_targets_only_for_prague_execution(
        [Values(null, Instruction.CALL, Instruction.BALANCE)] Instruction? access,
        [Values] bool prague,
        [Values] bool disableCode)
    {
        Address target = new("0x0000000000000000000000000000000000001003");
        byte[] delegation = Bytes.FromHexString("ef0100" + target.ToString(false, false));
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, delegation, Spec);
        TestState.CreateAccount(target, 1.Ether);
        TestState.InsertCode(target, new byte[] { 0x00 }, Spec);
        byte[] code = access switch
        {
            Instruction.CALL => Prepare.EvmCode.Call(TestItem.AddressC, 50000).Done,
            Instruction.BALANCE => Prepare.EvmCode.PushData(TestItem.AddressC).Op(Instruction.BALANCE).Done,
            _ => delegation
        };
        (Block block, Transaction transaction) = PrepareTx(
            prague ? MainnetSpecProvider.PragueActivation : MainnetSpecProvider.CancunActivation, 500000, code);
        IReleaseSpec spec = SpecProvider.GetSpec(block.Header);
        using GethLikeNativeTxTracer tracer = GethLikeNativeTracerFactory.CreateTracer(
            GetGethTraceOptions(JsonSerializer.Serialize(new { disableCode })), block, transaction, TestState, spec);

        _processor.Execute(transaction, new BlockExecutionContext(block.Header, spec), tracer);
        Dictionary<AddressAsKey, NativePrestateTracerAccount> accounts =
            (Dictionary<AddressAsKey, NativePrestateTracerAccount>)tracer.BuildResult().CustomTracerResult!.Value!;

        bool capturesTarget = prague && access != Instruction.BALANCE;
        Assert.That(accounts.ContainsKey(target), Is.EqualTo(capturesTarget));
        if (capturesTarget)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(accounts[target].Code.IsEmpty, Is.EqualTo(disableCode));
                Assert.That(accounts[target].CodeHash, Is.Not.Null);
            }
        }
    }

    [Test]
    public void Prestate_reads_a_called_account_code_once_per_transaction([Values] bool disableCode)
    {
        Address target = new("0x0000000000000000000000000000000000001003");
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, Bytes.FromHexString("ef0100" + target.ToString(false, false)), Spec);
        TestState.CreateAccount(target, 1.Ether);
        TestState.InsertCode(target, new byte[] { 0x00 }, Spec);
        byte[] code = Prepare.EvmCode
            .Call(TestItem.AddressC, 50000)
            .Call(TestItem.AddressC, 50000)
            .Call(TestItem.AddressC, 50000)
            .Done;

        Dictionary<AddressAsKey, NativePrestateTracerAccount> accounts = TracePrague(code, disableCode, out CodeReadCountingWorldState state);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accounts.ContainsKey(target), Is.True);
            Assert.That(state.CodeReads(TestItem.AddressC), Is.EqualTo(1));
        }
    }

    [Test]
    public void Prestate_follows_a_delegation_the_transaction_authorizes([Values] bool disableCode)
    {
        Address previous = new("0x0000000000000000000000000000000000001003");
        Address target = new("0x0000000000000000000000000000000000001004");
        TestState.CreateAccount(TestItem.AddressD, 1.Ether);
        TestState.InsertCode(TestItem.AddressD, Bytes.FromHexString("ef0100" + previous.ToString(false, false)), Spec);
        foreach (Address account in new[] { previous, target })
        {
            TestState.CreateAccount(account, 1.Ether);
            TestState.InsertCode(account, new byte[] { 0x00 }, Spec);
        }

        EthereumEcdsa ecdsa = new(SpecProvider.ChainId);
        Transaction transaction = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithAuthorizationCode(ecdsa.Sign(TestItem.PrivateKeyD, SpecProvider.ChainId, target, 0))
            .WithGasLimit(500000)
            .WithMaxFeePerGas(1)
            .WithMaxPriorityFeePerGas(1)
            .To(Recipient)
            .SignedAndResolved(ecdsa, SenderKey)
            .TestObject;
        byte[] code = Prepare.EvmCode.Call(TestItem.AddressD, 50000).Call(TestItem.AddressD, 50000).Done;

        Dictionary<AddressAsKey, NativePrestateTracerAccount> accounts = TracePrague(code, disableCode, out _, transaction);

        // The authority's prestate code is the old delegation; the calls run after the authorization replaced it.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accounts.ContainsKey(target), Is.True);
            Assert.That(accounts.ContainsKey(previous), Is.False);
        }
    }

    private Dictionary<AddressAsKey, NativePrestateTracerAccount> TracePrague(byte[] code, bool disableCode,
        out CodeReadCountingWorldState state, Transaction? transaction = null)
    {
        (Block block, Transaction tx) = PrepareTx(MainnetSpecProvider.PragueActivation, 500000, code, transaction: transaction);
        IReleaseSpec spec = SpecProvider.GetSpec(block.Header);
        state = new CodeReadCountingWorldState(TestState);
        using GethLikeNativeTxTracer tracer = GethLikeNativeTracerFactory.CreateTracer(
            GetGethTraceOptions(JsonSerializer.Serialize(new { disableCode })), block, tx, state, spec);
        _processor.Execute(tx, new BlockExecutionContext(block.Header, spec), tracer);
        return (Dictionary<AddressAsKey, NativePrestateTracerAccount>)tracer.BuildResult().CustomTracerResult!.Value!;
    }

    private sealed class CodeReadCountingWorldState(IWorldState state) : WorldStateDecorator(state)
    {
        private readonly Dictionary<AddressAsKey, int> _codeReads = [];

        public int CodeReads(Address address) => _codeReads.GetValueOrDefault(address);

        public override ReadOnlyMemory<byte> GetCode(Address address)
        {
            _codeReads[address] = CodeReads(address) + 1;
            return base.GetCode(address);
        }
    }

    [Test]
    public void Diff_selfdestruct_tracks_the_source_under_the_active_fork([Values] bool cancun, [Values] bool revert)
    {
        Address target = new("0x0000000000000000000000000000000000001003");
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, Prepare.EvmCode.PushData(target).Op(Instruction.SELFDESTRUCT).Done, Spec);
        TestState.CreateAccount(target, 1.Ether);
        Prepare parent = Prepare.EvmCode.Call(TestItem.AddressC, 50000).Op(Instruction.POP);
        byte[] code = (revert ? parent.PushData(0).PushData(0).Op(Instruction.REVERT) : parent.Op(Instruction.STOP)).Done;

        NativePrestateTracerDiffMode diff = ExecuteDiff(code,
            cancun ? MainnetSpecProvider.CancunActivation : MainnetSpecProvider.ShanghaiActivation);

        using (Assert.EnterMultipleScope())
        {
            // Geth's opcode deletion marker survives a reverted parent frame.
            Assert.That(diff.pre.ContainsKey(TestItem.AddressC), Is.EqualTo(!cancun || !revert));
            Assert.That(diff.post.ContainsKey(TestItem.AddressC), Is.EqualTo(cancun && !revert));
            Assert.That(diff.pre.ContainsKey(target), Is.EqualTo(!revert));
            Assert.That(diff.post.ContainsKey(target), Is.EqualTo(!revert));
            if (!revert)
                Assert.That(diff.post[target].Balance, Is.EqualTo(2.Ether));
        }
    }

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(false, true)]
    public void Diff_selfdestruct_omits_accounts_created_in_the_transaction(bool rootCreation, bool revert)
    {
        Address target = new("0x0000000000000000000000000000000000001003");
        TestState.CreateAccount(target, 1.Ether);
        byte[] initCode = Prepare.EvmCode.PushData(target).Op(Instruction.SELFDESTRUCT).Done;
        Prepare parent = Prepare.EvmCode.Create(initCode, 1).Op(Instruction.POP);
        byte[] code = rootCreation ? initCode
            : (revert ? parent.PushData(0).PushData(0).Op(Instruction.REVERT) : parent.Op(Instruction.STOP)).Done;
        Address created = ContractAddress.From(rootCreation ? TestItem.AddressA : TestItem.AddressB, 0);

        NativePrestateTracerDiffMode diff = ExecuteDiff(code, MainnetSpecProvider.CancunActivation, rootCreation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.pre.ContainsKey(created), Is.False);
            Assert.That(diff.post.ContainsKey(created), Is.False);
            Assert.That(diff.post.ContainsKey(target), Is.EqualTo(!revert));
            if (!revert)
                Assert.That(diff.post[target].Balance, Is.EqualTo(1.Ether + 1));
        }
    }

    [Test]
    public void Diff_create_preserves_a_prefunded_accounts_prestate([Values] bool create2)
    {
        byte[] initCode = [0x00];
        byte[] salt = new byte[32];
        Address created = create2 ? ContractAddress.From(TestItem.AddressB, salt, initCode)
            : ContractAddress.From(TestItem.AddressB, 0);
        TestState.CreateAccount(created, 100);
        byte[] code = (create2 ? Prepare.EvmCode.Create2(initCode, salt, 1) : Prepare.EvmCode.Create(initCode, 1)).Done;

        NativePrestateTracerDiffMode diff = ExecuteDiff(code, MainnetSpecProvider.CancunActivation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.pre[created].Balance, Is.EqualTo((UInt256)100));
            Assert.That(diff.post[created].Balance, Is.EqualTo((UInt256)101));
            Assert.That(diff.post[created].Nonce, Is.EqualTo((UInt256)1));
        }
    }

    private NativePrestateTracerDiffMode ExecuteDiff(byte[] code, ForkActivation activation, bool creation = false)
    {
        (Block block, Transaction transaction) = PrepareTx(activation, 500000, creation ? null : code);
        if (creation)
        {
            transaction.To = null;
            transaction.Data = code;
        }
        IReleaseSpec spec = SpecProvider.GetSpec(block.Header);
        using GethLikeNativeTxTracer tracer = GethLikeNativeTracerFactory.CreateTracer(
            GetGethTraceOptions(DiffMode), block, transaction, TestState, spec);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, spec), tracer);
        return (NativePrestateTracerDiffMode)tracer.BuildResult().CustomTracerResult!.Value!;
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

    private sealed class RecordingPrestateTracer(IWorldState worldState, bool disableStorage = false)
        : NativePrestateTracer(worldState, GetGethTraceOptions(JsonSerializer.Serialize(new { disableStorage })), Hash256.Zero, TestItem.AddressA, TestItem.AddressB)
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

    private sealed class StorageReadCounter
    {
        public int Count { get; set; }
        public bool FailNextRead { get; set; }
        public bool FailNextAccountRead { get; set; }
    }

    private sealed class RecordingStorageWorldState(IWorldState state, StorageReadCounter reads) : WorldStateDecorator(state)
    {
        public override void Get(in StorageCell storageCell, out UInt256 value)
        {
            reads.Count++;
            if (reads.FailNextRead)
            {
                reads.FailNextRead = false;
                throw new InvalidOperationException("storage read failed");
            }
            base.Get(in storageCell, out value);
        }

        public override bool TryGetAccount(Address address, out AccountStruct account)
        {
            if (reads.FailNextAccountRead)
            {
                reads.FailNextAccountRead = false;
                throw new InvalidOperationException("account read failed");
            }
            return base.TryGetAccount(address, out account);
        }
    }
}
