// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Test;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Taiko.Precompiles;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

/// <summary>
/// The Taiko VM's dispatch of a context-aware precompile handles an exception in this order: a native library that cannot
/// be used ends the process; a KZG setup fault or running out of memory is a node fault, so it escapes; any other exception
/// fails the call. Within a node fault, only a missing library directly in it or one level down ends the process.
/// </summary>
public class TaikoPrecompileFaultTests : VirtualMachineTestsBase
{
    private const int CallGas = 100_000;
    private const string NativeLoadFailureChildArgument = "--nethermind-taiko-precompile-native-load-failure";

    private TaikoEthereumVirtualMachine _taikoMachine = null!;

    [TearDown]
    public override void TearDown()
    {
        _taikoMachine?.Dispose();
        base.TearDown();
    }

    [Test]
    public void Node_fault_escapes_the_context_aware_precompile_call([ValueSource(nameof(NodeFaults))] Exception fault)
    {
        UseContextAwarePrecompile(new ThrowingPrecompile(fault));

        Assert.That(ExecuteCall, Throws.TypeOf(fault.GetType()));
    }

    private static IEnumerable<Exception> NodeFaults()
    {
        yield return new KzgSetupUnavailableException("KZG trusted setup failed to load");
        yield return new OutOfMemoryException();
        yield return new TypeInitializationException("Precompile", new OutOfMemoryException());
        // A missing library two levels down in a node fault does not end the process.
        yield return new KzgSetupUnavailableException("KZG trusted setup failed to load", new TypeInitializationException("Precompile", new DllNotFoundException("missing library")));
    }

    [Test]
    public void Other_context_aware_precompile_exception_fails_the_call()
    {
        UseContextAwarePrecompile(new ThrowingPrecompile(new InvalidOperationException("malformed input")));

        Assert.That(ExecuteCall(), Is.False);
    }

    [Test]
    public async Task Wrapped_wrong_format_library_ends_the_process_from_a_context_aware_call()
    {
        (int exitCode, string output) = await PrecompileFaultTests.RunInChildProcess(typeof(TaikoPrecompileFaultTests).Assembly, NativeLoadFailureChildArgument);

        Assert.That(exitCode, Is.EqualTo(ExitCodes.MissingPrecompile), output);
    }

    // Runs before the test host starts, so whatever ends a child process ends only that process.
    private static Task<int> Main(string[] args) => args switch
    {
        [NativeLoadFailureChildArgument] => Task.FromResult(RunNativeLoadFailureChild()),
        _ => MicrosoftTestingPlatformEntryPoint.Main(args)
    };

    /// <returns>1 when the call returns or throws instead of ending the process.</returns>
    private static int RunNativeLoadFailureChild()
    {
        TaikoPrecompileFaultTests fixture = new();
        try
        {
            fixture.Setup();
            fixture.UseContextAwarePrecompile(new ThrowingPrecompile(new TypeInitializationException("Precompile", new BadImageFormatException("wrong format"))));
            Console.Error.WriteLine($"The call returned instead of ending the process; it succeeded: {fixture.ExecuteCall()}.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"The call threw instead of ending the process: {exception}");
        }

        return 1;
    }

    private void UseContextAwarePrecompile(IPrecompile precompile)
    {
        Dictionary<AddressAsKey, CodeInfo> precompiles = new(new EthereumPrecompileProvider().GetPrecompiles())
        {
            [IdentityPrecompile.Address] = new(precompile)
        };
        IPrecompileProvider provider = new FixedPrecompileProvider(precompiles.ToFrozenDictionary());
        _taikoMachine = new TaikoEthereumVirtualMachine(new TestBlockhashProvider(SpecProvider), SpecProvider, Substitute.For<IL1OriginStore>(), LimboLogs.Instance);
        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, _taikoMachine,
            new CacheCodeInfoRepository(TestState, provider, new StaticCodeCache(16)), LimboLogs.Instance);
    }

    /// <returns>Whether the call to the precompile succeeded.</returns>
    private bool ExecuteCall()
    {
        byte[] bytecode = Prepare.EvmCode
            .Call(IdentityPrecompile.Address, CallGas)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        (Block block, Transaction transaction) = PrepareTx(Activation, 2 * CallGas, bytecode);
        TransactionResult result = _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), NullTxTracer.Instance);
        Assert.That(result.TransactionExecuted, Is.True, result.ToString());

        TestState.Get(new StorageCell(Recipient, UInt256.Zero), out UInt256 callSucceeded);
        return !callSucceeded.IsZero;
    }

    /// <summary>Throws from the context-aware overload, the one the Taiko VM must run.</summary>
    /// <remarks>The ordinary overload succeeds instead, so a call routed to the base dispatch fails every test here.</remarks>
    private sealed class ThrowingPrecompile(Exception exception) : IContextAwarePrecompile
    {
        public ulong BaseGasCost(IReleaseSpec releaseSpec) => 50_000;

        public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0;

        public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => Array.Empty<byte>();

        public Result<(byte[] returnValue, ulong gasConsumed)> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec, in PrecompileExtras extras) =>
            throw exception;
    }

    private sealed class FixedPrecompileProvider(FrozenDictionary<AddressAsKey, CodeInfo> precompiles) : IPrecompileProvider
    {
        public FrozenDictionary<AddressAsKey, CodeInfo> GetPrecompiles() => precompiles;
    }
}
