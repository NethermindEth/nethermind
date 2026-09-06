// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.State.Proofs;

namespace Nethermind.Merge.Plugin.Benchmark;

/// <summary>
/// Attributes the <c>engine_newPayload</c> serial prefix between transaction decode,
/// transactions-trie root, withdrawals root, and the complete
/// <see cref="ExecutionPayload.TryGetBlock"/> call.
/// </summary>
/// <remarks>
/// <see cref="HandlerPrefix"/> reproduces the <c>NewPayloadHandler.HandleAsync</c> order:
/// <c>TryGetTransactions</c> runs first (for sender recovery) on the handler thread, then
/// <c>TryGetBlock</c> starts the transactions-root task and blocks on it. Transactions are
/// real signed EIP-1559 transactions with a mainnet-like calldata mix, not opaque blobs,
/// so decode and trie-leaf costs are honest.
/// </remarks>
[MemoryDiagnoser]
public class NewPayloadPrefixBenchmarks
{
    // 200 transactions approximates the current mainnet median block; 400 a heavy one.
    [Params(100, 200, 400)]
    public int Txs;

    private ExecutionPayloadV3 _payload = null!;
    private byte[][] _encodedTransactions = null!;
    private Withdrawal[] _withdrawals = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _withdrawals = EngineBenchmarkHost.BuildWithdrawals(EngineBenchmarkHost.CapellaMaxWithdrawals);
        _payload = new ExecutionPayloadV3
        {
            ParentHash = TestItem.KeccakA,
            FeeRecipient = TestItem.AddressA,
            StateRoot = TestItem.KeccakB,
            ReceiptsRoot = TestItem.KeccakC,
            LogsBloom = Bloom.Empty,
            PrevRandao = TestItem.KeccakD,
            BlockNumber = 20_000_000,
            GasLimit = 36_000_000,
            GasUsed = 18_000_000,
            Timestamp = 1_700_100_000,
            ExtraData = new byte[32],
            BaseFeePerGas = 10_000_000_000,
            BlockHash = TestItem.KeccakE,
            BlobGasUsed = 6 * Eip4844Constants.GasPerBlob,
            ExcessBlobGas = 0x40000,
            ParentBeaconBlockRoot = TestItem.KeccakA,
            ExecutionRequests = [],
            Withdrawals = _withdrawals,
        };
        _payload.SetTransactions(BuildTransactions(Txs));
        _encodedTransactions = _payload.Transactions;
    }

    [Benchmark(Description = "TryGetTransactions (decode)")]
    public Transaction[] DecodeTransactions()
    {
        _payload.Transactions = _encodedTransactions; // resets the decoded-transactions memo
        return _payload.TryGetTransactions().Data!;
    }

    [Benchmark(Description = "TxTrie.CalculateRoot")]
    public Hash256 TxRoot() => TxTrie.CalculateRoot(_encodedTransactions);

    [Benchmark(Description = "WithdrawalTrie root")]
    public Hash256 WithdrawalsRoot() => new WithdrawalTrie(_withdrawals).RootHash;

    // No decode-memoized TryGetBlock arm: the memoized root task makes any in-loop measurement
    // either reuse the completed task or re-include decode; derive it as HandlerPrefix minus decode.

    [Benchmark(Description = "decode + TryGetBlock (handler order)", Baseline = true)]
    public Block HandlerPrefix()
    {
        _payload.Transactions = _encodedTransactions; // resets the decoded-transactions memo
        _payload.TryGetTransactions();
        return _payload.TryGetBlock().Data!;
    }

    [Benchmark(Description = "early root + decode + TryGetBlock")]
    public Block HandlerPrefixWithEarlyRoot()
    {
        _payload.Transactions = _encodedTransactions; // resets the decoded-transactions memo
        _payload.StartTxRootComputation();
        _payload.TryGetTransactions();
        return _payload.TryGetBlock().Data!;
    }

    private static Transaction[] BuildTransactions(int count)
    {
        Transaction[] transactions = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            // Rough mainnet mix: half plain transfers, the rest token transfers, swaps,
            // heavier contract calls, and an occasional data-heavy transaction.
            int callDataLength = (i % 20) switch
            {
                < 10 => 0,
                < 14 => 68,
                < 17 => 260,
                < 19 => 1024,
                _ => 8192,
            };

            TransactionBuilder<Transaction> builder = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithChainId(BlockchainIds.Mainnet)
                .WithNonce((ulong)i)
                .WithGasLimit(callDataLength == 0 ? 21_000UL : 90_000UL + (ulong)callDataLength * 16)
                .WithMaxFeePerGas(30 * Unit.GWei)
                .WithMaxPriorityFeePerGas(1 * Unit.GWei)
                .WithValue(1 * Unit.Ether)
                .WithTo(TestItem.Addresses[i % TestItem.Addresses.Length]);

            if (callDataLength > 0)
            {
                byte[] callData = new byte[callDataLength];
                new Random(i).NextBytes(callData);
                builder = builder.WithData(callData);
            }

            transactions[i] = builder.SignedAndResolved().TestObject;
        }

        return transactions;
    }
}
