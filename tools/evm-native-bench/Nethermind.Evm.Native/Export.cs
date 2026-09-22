using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Evm.Native;

internal sealed class NullBlockhashProvider : IBlockhashProvider
{
    public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec) => null;
    public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => Task.CompletedTask;
}

/// One EVM per caller thread: ITransactionProcessor and IVirtualMachine hold mutable per-block
/// context, so they are not shareable. The handle is what the host owns.
internal sealed class Engine
{
    public required ITransactionProcessor Processor { get; init; }
    public required IDisposable Scope { get; init; }
    public Address Sender { get; init; } = Address.Zero;
    public CallbackScopeProvider? Callbacks { get; init; }
    public ulong Nonce;
}

public static class EvmNative
{
    private static readonly Address Recipient = new("0x2222222222222222222222222222222222222222");
    private static readonly Address LoopContract = new("0x3333333333333333333333333333333333333333");

    /// A recipient nobody has read before, so the tx must go to the state source for it.
    private static Address FreshRecipient(ulong n)
    {
        byte[] b = new byte[20];
        b[0] = 0x44;
        BitConverter.TryWriteBytes(b.AsSpan(12), n);
        return new Address(b);
    }

    /// Builds one EVM stack and returns an opaque handle, or 0 on failure.
    /// `seed` gives each engine its own sender so the engines share no state.
    [UnmanagedCallersOnly(EntryPoint = "nm_evm_engine_create")]
    public static IntPtr EngineCreate(int seed)
    {
        try
        {
            ILogManager logs = NullLogManager.Instance;   // LimboTraceLogger.IsTrace is true by design: it forces log-string construction
            ISpecProvider specs = MainnetSpecProvider.Instance;

            WorldState state = new(
                new TrieStoreScopeProvider(new RawTrieStore(new MemDb()), new MemDb(), logs), logs);

            EthereumTransactionProcessor processor = new(
                BlobBaseFeeCalculator.Instance, specs, state,
                new EthereumVirtualMachine(new NullBlockhashProvider(), specs, logs),
                new EthereumCodeInfoRepository(state), logs);

            IDisposable scope = state.BeginScope(null);

            // Block 1 selects Frontier. Pin Prague so this is comparable with revm's default.
            BlockHeader header = new(
                Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero,
                UInt256.Zero, MainnetSpecProvider.ParisBlockNumber + 3,
                100_000_000_000L, MainnetSpecProvider.PragueBlockTimestamp, [])
            {
                StateRoot = Keccak.EmptyTreeHash,
                BaseFeePerGas = UInt256.Zero,
                ExcessBlobGas = 0,
                BlobGasUsed = 0,
            };

            byte[] senderBytes = new byte[20];
            senderBytes[0] = 0x11;
            BitConverter.TryWriteBytes(senderBytes.AsSpan(16), seed);
            Address sender = new(senderBytes);

            state.CreateAccount(sender, UInt256.Parse("1000000000000000000000000"));

            // 10,000 iterations of PUSH1/ADD/DUP1/PUSH2/GT/PUSH1/JUMPI ~ 290k gas of pure interpreter.
            byte[] loopCode = Convert.FromHexString("60005b600101806127101160025700");
            state.CreateAccount(LoopContract, UInt256.Zero);
            state.InsertCode(LoopContract, ValueKeccak.Compute(loopCode), loopCode, specs.GetSpec(header));

            state.Commit(specs.GetSpec(header));
            state.CommitTree(0);
            processor.SetBlockExecutionContext(header);

            Engine engine = new() { Processor = processor, Scope = scope, Sender = sender };
            return GCHandle.ToIntPtr(GCHandle.Alloc(engine));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[managed] engine create failed: " + e);
            return IntPtr.Zero;
        }
    }

    /// Same engine, but every state read the scope has not cached goes out to the host.
    /// The host must already hold the sender and the loop contract.
    [UnmanagedCallersOnly(EntryPoint = "nm_evm_engine_create_cb")]
    public static unsafe IntPtr EngineCreateWithCallbacks(
        int seed,
        delegate* unmanaged<byte*, byte*, byte*, byte*, int> getAccount,
        delegate* unmanaged<byte*, byte*, byte*, int> getStorage,
        delegate* unmanaged<byte*, byte*, int, int> getCode)
    {
        try
        {
            ILogManager logs = NullLogManager.Instance;   // LimboTraceLogger.IsTrace is true by design: it forces log-string construction
            ISpecProvider specs = MainnetSpecProvider.Instance;

            CallbackScopeProvider provider = new(new StateCallbacks
            {
                GetAccount = getAccount, GetStorage = getStorage, GetCode = getCode,
            });
            WorldState state = new(provider, logs);

            EthereumTransactionProcessor processor = new(
                BlobBaseFeeCalculator.Instance, specs, state,
                new EthereumVirtualMachine(new NullBlockhashProvider(), specs, logs),
                new EthereumCodeInfoRepository(state), logs);

            IDisposable scope = state.BeginScope(null);

            BlockHeader header = new(
                Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero,
                UInt256.Zero, MainnetSpecProvider.ParisBlockNumber + 3,
                100_000_000_000L, MainnetSpecProvider.PragueBlockTimestamp, [])
            {
                StateRoot = Keccak.EmptyTreeHash,
                BaseFeePerGas = UInt256.Zero,
                ExcessBlobGas = 0,
                BlobGasUsed = 0,
            };
            processor.SetBlockExecutionContext(header);

            Engine engine = new()
            {
                Processor = processor, Scope = scope, Sender = HostSender, Callbacks = provider,
            };
            return GCHandle.ToIntPtr(GCHandle.Alloc(engine));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[managed] callback engine create failed: " + e);
            return IntPtr.Zero;
        }
    }

    /// The sender the host is expected to hold when callbacks supply the state.
    public static readonly Address HostSender = new("0x1111111111111111111111111111111111111111");

    [UnmanagedCallersOnly(EntryPoint = "nm_evm_engine_reads")]
    public static unsafe void EngineReads(IntPtr handle, long* accounts, long* storage, long* code)
    {
        CallbackScopeProvider? p = (GCHandle.FromIntPtr(handle).Target as Engine)?.Callbacks;
        *accounts = p?.AccountReads ?? 0;
        *storage = p?.StorageReads ?? 0;
        *code = p?.CodeReads ?? 0;
    }

    /// Runs <paramref name="count"/> transactions. workload: 0 = transfer to a fixed
    /// recipient, 1 = call the loop contract, 2 = transfer to a recipient nobody has read.
    [UnmanagedCallersOnly(EntryPoint = "nm_evm_engine_run")]
    public static long EngineRun(IntPtr handle, int count, int workload)
    {
        if (handle == IntPtr.Zero) return -1;
        Engine engine = (Engine)GCHandle.FromIntPtr(handle).Target!;
        try
        {
            for (int i = 0; i < count; i++)
            {
                Transaction tx = new()
                {
                    Type = TxType.Legacy,
                    Nonce = engine.Nonce++,
                    GasLimit = workload == 1 ? 1_000_000UL : 100_000UL,
                    GasPrice = UInt256.One,
                    To = workload switch { 0 => Recipient, 1 => LoopContract, _ => FreshRecipient(engine.Nonce) },
                    Value = workload == 1 ? UInt256.Zero : UInt256.One,
                    SenderAddress = engine.Sender,
                    Data = null,
                };
                TransactionResult r = engine.Processor.Execute(tx, NullTxTracer.Instance);
                if (!r) { Console.Error.WriteLine("[managed] tx failed: " + r); return -2; }
            }
            return (long)count * (workload == 1 ? 311_000 : 21_000);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[managed] run failed: " + e);
            return -3;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "nm_evm_engine_destroy")]
    public static void EngineDestroy(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        GCHandle h = GCHandle.FromIntPtr(handle);
        (h.Target as Engine)?.Scope.Dispose();
        h.Free();
    }

    [UnmanagedCallersOnly(EntryPoint = "nm_evm_gc_stats")]
    public static unsafe void GcStats(long* allocatedBytes, int* gen0, int* gen1, int* gen2, long* heapBytes)
    {
        *allocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        *gen0 = GC.CollectionCount(0);
        *gen1 = GC.CollectionCount(1);
        *gen2 = GC.CollectionCount(2);
        *heapBytes = GC.GetTotalMemory(forceFullCollection: false);
    }
}
