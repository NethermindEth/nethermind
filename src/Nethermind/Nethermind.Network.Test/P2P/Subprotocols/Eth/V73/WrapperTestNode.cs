// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NSubstitute;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;

/// <summary>Accepts every proof unless told otherwise, counting recursive STARK checks.</summary>
internal sealed class WrapperTestVerifier : ILeanProofVerifier
{
    private int _calls;
    public bool Valid { get; set; } = true;
    public int Calls => Volatile.Read(ref _calls);
    public void EnsureAvailable() { }
    public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => Valid;

    /// <summary>When set, recursive STARK checks throw this instead of returning a verdict.</summary>
    public Exception? Throws { get; set; }

    /// <summary>When set, recursive STARK checks wait for it, as a slow verification would.</summary>
    public ManualResetEventSlim? Hold { get; set; }

    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
    {
        Interlocked.Increment(ref _calls);
        Hold?.Wait(TimeSpan.FromSeconds(10));
        if (Throws is { } exception) throw exception;
        return Valid;
    }

    public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input) => [1];
}

/// <summary>Records what the transport sends to one peer and the penalties it applies.</summary>
internal sealed class FakeWrapperLink(string name = "peer") : IWrapperLink
{
    private readonly List<P2PMessage> _sent = [];
    private readonly List<string> _penalties = [];

    public string Description => name;

    /// <summary>Runs on the sending thread before a message is recorded.</summary>
    public Action<P2PMessage>? OnSend { get; set; }

    public void Send(P2PMessage message)
    {
        OnSend?.Invoke(message);
        lock (_sent) _sent.Add(message);
    }

    public void Penalize(string reason) { lock (_penalties) _penalties.Add(reason); }

    public T[] Sent<T>() where T : P2PMessage { lock (_sent) return [.. _sent.OfType<T>()]; }
    public string[] Penalties { get { lock (_penalties) return [.. _penalties]; } }
    public void Clear() { lock (_sent) _sent.Clear(); }
}

/// <summary>A transport over substitute chain components, with valid-by-default proofs and a manual clock.</summary>
internal sealed class WrapperTestNode : IDisposable
{
    public WrapperTestNode(ISpecProvider? specProvider = null)
    {
        Time = new ManualTimeProvider();
        ISpecProvider specs = specProvider ?? new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance);
        Tree.ChainId.Returns(1UL);
        Tree.Head.Returns(Build.A.Block.WithNumber(10).TestObject);
        Pool.GetPendingTransactions().Returns(_ => { lock (Pending) return [.. Pending]; });
        Pool.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>());
        Pool.TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction?>()).Returns(call =>
        {
            Transaction? found;
            lock (Pending) found = Pending.Find(t => t.Hash!.ValueHash256 == call.Arg<ValueHash256>());
            call[1] = found;
            return found is not null;
        });
        Pool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(call =>
        {
            lock (Pending) Pending.Add(call.Arg<Transaction>());
            return AcceptTxResult.Accepted;
        });
        Wrappers = new ProofWrapperService(Pool, specs, Tree, ProofStore, Verifier);
        Transport = new ProofWrapperTransport(Wrappers, LimboLogs.Instance, Time);
    }

    public ManualTimeProvider Time { get; }
    public IBlockTree Tree { get; } = Substitute.For<IBlockTree>();
    public ITxPool Pool { get; } = Substitute.For<ITxPool>();
    public WrapperTestVerifier Verifier { get; } = new();
    public List<Transaction> Pending { get; } = [];
    public LeanProofStore ProofStore { get; } = new(FrameTxSigHash.ComputeValue);
    public ProofWrapperService Wrappers { get; }
    public ProofWrapperTransport Transport { get; }

    /// <summary>Moves the local head to <paramref name="number"/> at <paramref name="timestamp"/>, with a parent one EIP-7843 slot
    /// of <paramref name="slotLength"/> seconds earlier, so the target fork is the one active at <c>timestamp + slotLength</c>.</summary>
    public void SetHead(ulong number, ulong timestamp, ulong slotLength = 10)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(number - 1).WithTimestamp(timestamp - slotLength).WithSlotNumber(number - 1).TestObject;
        Block head = Build.A.Block.WithParent(parent).WithNumber(number).WithTimestamp(timestamp).WithSlotNumber(number).TestObject;
        Tree.FindHeader(parent.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(parent);
        Tree.Head.Returns(head);
    }

    public WrapperPeer Connect(FakeWrapperLink link) =>
        Transport.Accept(link) ?? throw new InvalidOperationException("Wrapper exchange is inactive");

    public void Tick(TimeSpan elapsed) => Time.AdvanceAndFireTimer(elapsed);

    public static async Task Until(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    public void Dispose() => Transport.Dispose();
}

internal static class WrapperTestObjects
{
    /// <summary>A frame transaction with one leanSPHINCS dependency.</summary>
    public static Transaction FrameTransaction(int seed)
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute(BitConverter.GetBytes(seed)), default);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = 1,
            SenderAddress = Address.Zero,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        transaction.Hash = new Hash256(ValueKeccak.Compute(Envelope(transaction)));
        return transaction;
    }

    /// <summary>The transaction's pooled encoding, as a wrapper carries it.</summary>
    public static byte[] Envelope(Transaction transaction) => MempoolWrapperDecoder.EncodeTransaction(transaction);

    /// <summary>A mode-1 wrapper over <paramref name="transactions"/> with an opaque proof.</summary>
    public static byte[] Wrapper(int proofBytes, params Transaction[] transactions) => Wrapper(proofBytes, proofBytes, transactions);

    /// <summary>A mode-1 wrapper whose opaque proof is drawn from <paramref name="seed"/>, so wrappers of equal size
    /// over the same transactions differ.</summary>
    public static byte[] Wrapper(int proofBytes, int seed, params Transaction[] transactions)
    {
        Transaction[] sorted = [.. transactions.OrderBy(t => t.Hash!.ToString())];
        List<FrameDependency> deps = Eip8288Dependencies.Canonicalize(sorted.SelectMany(tx => Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue)));
        byte[] proof = new byte[proofBytes];
        new Random(seed).NextBytes(proof);
        return MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = sorted,
            Deps = deps,
            Mode = MempoolWrapper.ModeRecursive,
            RecursiveStark = new RecursiveStark(proof, new Hash256(Eip8288Dependencies.ComputeDepsHash(deps)))
        }).Bytes;
    }

    /// <summary>A mode-0 wrapper over <paramref name="transactions"/> with one opaque witness per dependency.</summary>
    public static byte[] DirectWrapper(params Transaction[] transactions)
    {
        Transaction[] sorted = [.. transactions.OrderBy(t => t.Hash!.ToString())];
        List<FrameDependency> deps = Eip8288Dependencies.Canonicalize(sorted.SelectMany(tx => Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue)));
        return MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = sorted,
            Deps = deps,
            Mode = MempoolWrapper.ModeDirect,
            Proofs = [.. deps.Select(static _ => new byte[] { 1 })]
        }).Bytes;
    }

    public static WrapperAnnouncement Describe(byte[] wrapper) => WrapperAnnouncement.Create(wrapper);

    /// <summary>A ProofWrappers entry pairing <paramref name="wrapper"/> with its canonical hash.</summary>
    public static ProofWrapperEntry Entry(byte[] wrapper) => new(ValueKeccak.Compute(wrapper), wrapper);
}
