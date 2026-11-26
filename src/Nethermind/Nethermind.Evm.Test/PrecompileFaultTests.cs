// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// The precompile dispatch handles an exception in this order: a native library that cannot be used ends the process; a
/// KZG setup fault or running out of memory is a node fault, so it escapes; any other exception fails the call. Within a
/// node fault, only a missing library directly in it or one level down ends the process: a KZG setup fault caused
/// directly by a missing library exits, one caused directly by a library built for another platform escapes.
/// Both dispatch paths are covered: CALL runs a precompile in a call frame, and an untraced STATICCALL runs it inline.
/// </summary>
public class PrecompileFaultTests : VirtualMachineTestsBase
{
    private const int CallGas = 100_000;
    private const int PointEvaluationInputLength = 192;

    internal const string NativeLoadFailureChildArgument = "--nethermind-precompile-native-load-failure";
    private const string LoggedLoadFailureMarker = "The VM logged the load failure itself.";

    private ILogManager _logManager = LimboLogs.Instance;

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;

    protected override ILogManager GetLogManager() => _logManager;

    [OneTimeSetUp]
    public Task LoadKzgSetup() => KzgPolynomialCommitments.InitializeAsync();

    [Test]
    public async Task Wrong_format_library_ends_the_process_from_a_framed_call_with_error_logging_off()
    {
        (int exitCode, string output) = await RunInChildProcess(typeof(PrecompileFaultTests).Assembly, NativeLoadFailureChildArgument, nameof(Instruction.CALL));

        Assert.That(exitCode, Is.EqualTo(ExitCodes.MissingPrecompile), output);
    }

    [Test]
    public async Task Missing_export_two_levels_down_ends_the_process_from_an_inline_call_and_is_logged_itself()
    {
        (int exitCode, string output) = await RunInChildProcess(typeof(PrecompileFaultTests).Assembly, NativeLoadFailureChildArgument, nameof(Instruction.STATICCALL));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(ExitCodes.MissingPrecompile), output);
            Assert.That(output, Does.Contain(LoggedLoadFailureMarker));
        }
    }

    /// <summary>Makes, in this process, the call that the VM must end the process on; started by the test assembly's entry point.</summary>
    /// <returns>1 when the call returns or throws instead.</returns>
    internal static int RunNativeLoadFailureChild(string call)
    {
        PrecompileFaultTests fixture = new();
        Exception thrown;
        if (call == nameof(Instruction.CALL))
        {
            // Error logging off: the process must end all the same.
            thrown = new BadImageFormatException("wrong format");
            fixture._logManager = NullLogManager.Instance;
        }
        else
        {
            EntryPointNotFoundException loadFailure = new("missing export");
            thrown = new TypeInitializationException("Precompile", new InvalidOperationException("wrapper", loadFailure));
            fixture._logManager = new OneLoggerLogManager(new ILogger(new LoadFailureLogger(loadFailure)));
        }

        try
        {
            fixture.Setup();
            fixture.UsePointEvaluationPrecompile(new ThrowingPrecompile(thrown));
            bool succeeded = fixture.ExecuteCall(Enum.Parse<Instruction>(call), new byte[PointEvaluationInputLength]);
            Console.Error.WriteLine($"The call returned instead of ending the process; it succeeded: {succeeded}.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"The call threw instead of ending the process: {exception}");
        }

        return 1;
    }

    /// <summary>Runs the entry point of <paramref name="assembly"/> in a child process, for a test of code that ends the process.</summary>
    /// <remarks>Both pipes are drained while the child runs. One two-minute deadline covers the exit and both pipes, which
    /// a descendant of the child can hold open after the child has exited; past it the test fails without waiting further,
    /// and the reads are cancelled and the pipes closed, so that neither outlives the test.</remarks>
    public static async Task<(int ExitCode, string Output)> RunInChildProcess(Assembly assembly, params string[] arguments)
    {
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(assembly.Location);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process child = new() { StartInfo = start };
        Assert.That(child.Start(), Is.True);
        using CancellationTokenSource abandon = new();
        // Disposed in reverse order on the way out: the readers, which close the pipes, then the token source.
        using StreamReader standardOutput = child.StandardOutput;
        using StreamReader standardError = child.StandardError;
        Task<string> output = standardOutput.ReadToEndAsync(abandon.Token);
        Task<string> error = standardError.ReadToEndAsync(abandon.Token);
        try
        {
            await Task.WhenAll(child.WaitForExitAsync(), output, error).WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            try
            {
                child.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The child may have exited while a descendant still holds a pipe.
            }

            string failure = $"The child process or a process holding its pipes did not end within two minutes (child exited: {child.HasExited}).\n{ReadSoFar(output)}\n{ReadSoFar(error)}";
            abandon.Cancel();
            Assert.Fail(failure);
        }

        return (child.ExitCode, $"{await output}\n{await error}");

        static string ReadSoFar(Task<string> pipe) => pipe.IsCompletedSuccessfully ? pipe.Result : "(pipe still open)";
    }

    [Test]
    public void Node_fault_escapes_the_point_evaluation_call(
        [Values(Instruction.CALL, Instruction.STATICCALL)] Instruction call,
        [ValueSource(nameof(NodeFaults))] Exception fault)
    {
        UsePointEvaluationPrecompile(new ThrowingPrecompile(fault));

        Assert.That(() => ExecuteCall(call, new byte[PointEvaluationInputLength]), Throws.TypeOf(fault.GetType()));
    }

    private static IEnumerable<Exception> NodeFaults()
    {
        yield return new KzgSetupUnavailableException("KZG trusted setup failed to load");
        yield return new OutOfMemoryException();
        yield return new TypeInitializationException("Precompile", new OutOfMemoryException());
    }

    [TestCase(Instruction.CALL)]
    [TestCase(Instruction.STATICCALL)]
    public void Other_precompile_exception_still_fails_the_call(Instruction call)
    {
        UsePointEvaluationPrecompile(new ThrowingPrecompile(new InvalidOperationException("malformed input")));

        Assert.That(ExecuteCall(call, new byte[PointEvaluationInputLength]), Is.False);
    }

    [TestCaseSource(nameof(MalformedPointEvaluationCalls))]
    public void Malformed_point_evaluation_input_fails_the_call(Instruction call, byte[] input) =>
        Assert.That(ExecuteCall(call, input), Is.False);

    private static IEnumerable<TestCaseData> MalformedPointEvaluationCalls()
    {
        // The commitment is not a curve point, so the proof check itself throws and reports it as invalid.
        byte[] offCurve = new byte[PointEvaluationInputLength];
        offCurve.AsSpan(96, 48).Fill(0xff);
        KzgPolynomialCommitments.TryComputeCommitmentHashV1(offCurve.AsSpan(96, 48), offCurve.AsSpan(0, 32));

        foreach (Instruction call in (Instruction[])[Instruction.CALL, Instruction.STATICCALL])
        {
            yield return new TestCaseData(call, new byte[PointEvaluationInputLength - 1]).SetArgDisplayNames(call.ToString(), "short input");
            yield return new TestCaseData(call, new byte[PointEvaluationInputLength]).SetArgDisplayNames(call.ToString(), "hash mismatch");
            yield return new TestCaseData(call, offCurve).SetArgDisplayNames(call.ToString(), "off-curve commitment");
        }
    }

    private void UsePointEvaluationPrecompile(IPrecompile precompile)
    {
        Dictionary<AddressAsKey, CodeInfo> precompiles = new(new EthereumPrecompileProvider().GetPrecompiles())
        {
            [KzgPointEvaluationPrecompile.Address] = new(precompile)
        };
        IPrecompileProvider provider = new FixedPrecompileProvider(precompiles.ToFrozenDictionary());
        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, Machine,
            new CacheCodeInfoRepository(TestState, provider, new StaticCodeCache(16)), LimboLogs.Instance);
    }

    /// <returns>Whether the call to the point evaluation precompile succeeded.</returns>
    private bool ExecuteCall(Instruction call, byte[] input)
    {
        Prepare code = Prepare.EvmCode
            .StoreDataInMemory(0, input)
            .PushData(0)
            .PushData(0)
            .PushData(input.Length)
            .PushData(0);
        if (call == Instruction.CALL) code.PushData(0);
        byte[] bytecode = code
            .PushData(KzgPointEvaluationPrecompile.Address)
            .PushData(CallGas)
            .Op(call)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        // Untraced, so an eligible STATICCALL takes the inline precompile path.
        (Block block, Transaction transaction) = PrepareTx(Activation, 2 * CallGas, bytecode);
        TransactionResult result = _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), NullTxTracer.Instance);
        Assert.That(result.TransactionExecuted, Is.True, result.ToString());

        TestState.Get(new StorageCell(Recipient, UInt256.Zero), out UInt256 callSucceeded);
        return !callSucceeded.IsZero;
    }

    private sealed class ThrowingPrecompile(Exception exception) : IPrecompile
    {
        public ulong BaseGasCost(IReleaseSpec releaseSpec) => 50_000;

        public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0;

        public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => throw exception;
    }

    private sealed class FixedPrecompileProvider(FrozenDictionary<AddressAsKey, CodeInfo> precompiles) : IPrecompileProvider
    {
        public FrozenDictionary<AddressAsKey, CodeInfo> GetPrecompiles() => precompiles;
    }

    /// <summary>Writes <see cref="LoggedLoadFailureMarker"/> when an error is logged with <paramref name="loadFailure"/> itself.</summary>
    private sealed class LoadFailureLogger(Exception loadFailure) : InterfaceLogger
    {
        public void Error(string text, Exception? ex = null)
        {
            if (ReferenceEquals(ex, loadFailure)) Console.WriteLine(LoggedLoadFailureMarker);
        }

        public void Info(string text) { }
        public void Warn(string text) { }
        public void Debug(string text) { }
        public void Trace(string text) { }
        public bool IsInfo => false;
        public bool IsWarn => false;
        public bool IsDebug => false;
        public bool IsTrace => false;
        public bool IsError => true;
    }
}
