using System;
using System.Diagnostics;
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

namespace Nethermind.Evm.TraceHarness;

internal sealed class NullBlockhashProvider : IBlockhashProvider
{
    public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec) => null;
    public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => Task.CompletedTask;
}

public static class Program
{
    private static readonly Address Sender = new("0x1111111111111111111111111111111111111111");
    private static readonly Address Recipient = new("0x2222222222222222222222222222222222222222");
    private static readonly Address LoopContract = new("0x3333333333333333333333333333333333333333");

    private static Address FreshRecipient(ulong n)
    {
        byte[] b = new byte[20];
        b[0] = 0x44;
        BitConverter.TryWriteBytes(b.AsSpan(12), n);
        return new Address(b);
    }

    public static int Main(string[] args)
    {
        int workload = args.Length > 0 ? int.Parse(args[0]) : 0;
        int count = args.Length > 1 ? int.Parse(args[1]) : 200_000;

        ILogManager logs = NullLogManager.Instance;   // LimboTraceLogger.IsTrace is true by design: it forces log-string construction
        ISpecProvider specs = MainnetSpecProvider.Instance;

        WorldState state = new(
            new TrieStoreScopeProvider(new RawTrieStore(new MemDb()), new MemDb(), logs), logs);

        EthereumTransactionProcessor processor = new(
            BlobBaseFeeCalculator.Instance, specs, state,
            new EthereumVirtualMachine(new NullBlockhashProvider(), specs, logs),
            new EthereumCodeInfoRepository(state), logs);

        using IDisposable scope = state.BeginScope(null);

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
        IReleaseSpec spec = specs.GetSpec(header);

        state.CreateAccount(Sender, UInt256.Parse("1000000000000000000000000"));
        byte[] loopCode = Convert.FromHexString("60005b600101806127101160025700");
        state.CreateAccount(LoopContract, UInt256.Zero);
        state.InsertCode(LoopContract, ValueKeccak.Compute(loopCode), loopCode, spec);
        state.Commit(spec);
        state.CommitTree(0);
        processor.SetBlockExecutionContext(header);

        Console.WriteLine($"warming up (workload={workload})...");
        Run(processor, 5_000, workload);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch sw = Stopwatch.StartNew();
        Console.WriteLine($"MEASURE START {count} txs");
        Run(processor, count, workload);
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"MEASURE END   {count} txs in {sw.Elapsed.TotalSeconds:F2} s, "
                          + $"{sw.Elapsed.TotalMilliseconds * 1000 / count:F2} us/tx, "
                          + $"{(double)allocated / count:F0} B/tx allocated");
        return 0;
    }

    private static ulong _nonce;

    private static void Run(ITransactionProcessor processor, int count, int workload)
    {
        for (int i = 0; i < count; i++)
        {
            Transaction tx = new()
            {
                Type = TxType.Legacy,
                Nonce = _nonce++,
                GasLimit = workload == 1 ? 1_000_000UL : 100_000UL,
                GasPrice = UInt256.One,
                To = workload switch { 0 => Recipient, 1 => LoopContract, _ => FreshRecipient(_nonce) },
                Value = workload == 1 ? UInt256.Zero : UInt256.One,
                SenderAddress = Sender,
                Data = null,
            };
            TransactionResult r = processor.Execute(tx, NullTxTracer.Instance);
            if (!r) throw new InvalidOperationException("tx failed: " + r);
        }
    }
}
