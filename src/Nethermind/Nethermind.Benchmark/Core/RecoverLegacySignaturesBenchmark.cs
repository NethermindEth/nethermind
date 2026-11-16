// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Sender recovery of a block of legacy EIP-155 transactions, each one either recovered first on pool ingress
/// (<see cref="SeenInPool"/>) or never seen before.
/// </summary>
/// <remarks>
/// A block recovery sets every sender, so each iteration is a single invocation that starts from senders reset
/// by <see cref="IterationSetupAttribute"/>.
/// </remarks>
[MemoryDiagnoser]
[InvocationCount(1)]
[WarmupCount(5)]
[MinIterationCount(40)]
[MaxIterationCount(80)]
public class RecoverLegacySignaturesBenchmark
{
    private const int TxCount = 100;

    private EthereumEcdsa _ecdsa;
    private RecoverSignatures _sut;
    private Block _block;

    [Params(false, true)]
    public bool SeenInPool { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        ISpecProvider specProvider = MainnetSpecProvider.Instance;
        _ecdsa = new EthereumEcdsa(specProvider.ChainId);
        _sut = new RecoverSignatures(_ecdsa, specProvider, NullLogManager.Instance);

        Transaction[] txs = new Transaction[TxCount];
        byte[][] encoded = new byte[TxCount][];
        for (int i = 0; i < TxCount; i++)
        {
            txs[i] = Build.A.Transaction
                .WithType(TxType.Legacy)
                .WithNonce((ulong)i)
                .WithData(new byte[100])
                .SignedAndResolved(_ecdsa, Build.A.PrivateKey.TestObject)
                .WithSenderAddress(null)
                .TestObject;
            encoded[i] = TxDecoder.Instance.Encode(txs[i], RlpBehaviors.SkipTypedWrapping);
        }

        _block = Build.A.Block
            .WithHeader(new BlockHeader { Timestamp = ulong.MaxValue, Number = long.MaxValue })
            .WithTransactions(txs)
            .TestObject;
        _block.EncodedTransactions = encoded;
    }

    [IterationSetup]
    public void IterationSetup()
    {
        EthereumEcdsaExtensions.ClearSenderCache();
        foreach (Transaction tx in _block.Transactions)
        {
            tx.SenderAddress = null;
            if (SeenInPool)
            {
                _ecdsa.TryRecoverAddress(tx, out _);
            }
        }
    }

    [Benchmark]
    public void RecoverLegacyBlock() => _sut.RecoverData(_block);
}
