// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs.Forks;
using Nethermind.State.Proofs;

namespace Nethermind.Merge.Plugin.Benchmark;

/// <summary>
/// Attributes the <c>engine_newPayload</c> serial prefix between transaction decode,
/// transactions-trie root, withdrawals root, and the complete
/// <see cref="ExecutionPayload.TryGetBlock"/> call.
/// </summary>
/// <remarks>
/// <see cref="HandlerPrefix"/> validates parameters before starting root preparation.
/// <see cref="HandlerPrefixWithEarlyRoot"/> measures speculative root preparation alongside
/// parameter validation. Both use signed EIP-1559 transactions with a mainnet-like calldata
/// mix to include transaction-decoding and trie-leaf costs.
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
    private ExecutionPayloadParams<ExecutionPayloadV3> _parameters = null!;

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
        _parameters = new(_payload, [], TestItem.KeccakA);
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
    public Hash256 WithdrawalsRoot() => WithdrawalTrie.CalculateRoot(_withdrawals);

    [Benchmark(Description = "validate + late root + TryGetBlock", Baseline = true)]
    public Block HandlerPrefix()
    {
        _payload.Transactions = _encodedTransactions; // resets the decoded-transactions memo
        ValidateParams();
        using ExecutionPayloadPreparation preparation = new(_payload);
        return preparation.TryGetBlock().Data!;
    }

    [Benchmark(Description = "early root + validate + TryGetBlock")]
    public Block HandlerPrefixWithEarlyRoot()
    {
        _payload.Transactions = _encodedTransactions; // resets the decoded-transactions memo
        using ExecutionPayloadPreparation preparation = new(_payload);
        using (preparation.Workers.Enter())
            ValidateParams();
        return preparation.TryGetBlock().Data!;
    }

    private void ValidateParams()
    {
        if (_parameters.ValidateParams(Cancun.Instance, EngineApiVersions.NewPayload.V3, out string? error) != Data.ValidationResult.Success)
            throw new InvalidOperationException(error);
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
