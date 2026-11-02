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
using Nethermind.Crypto;
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
    public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => Valid;

    /// <summary>When set, recursive STARK checks wait for it, as a slow verification would.</summary>
    public ManualResetEventSlim? Hold { get; set; }

    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
    {
        Interlocked.Increment(ref _calls);
        Hold?.Wait(TimeSpan.FromSeconds(10));
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

    public void Send(P2PMessage message)
    {
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
        Wrappers = new ProofWrapperService(Pool, specs, Tree, ProofStore, Verifier, new ForwardingBlobAdmission(this));
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

    /// <summary>Where validated wrappers hand their blob-carrying transactions; none refuses them.</summary>
    public IWrappedBlobTransactionAdmission? BlobAdmission { get; set; }

    private sealed class ForwardingBlobAdmission(WrapperTestNode node) : IWrappedBlobTransactionAdmission
    {
        public AcceptTxResult Admit(Transaction transaction, object? source, IDisposable proofPin)
        {
            if (node.BlobAdmission is { } target) return target.Admit(transaction, source, proofPin);
            proofPin.Dispose();
            return AcceptTxResult.Invalid;
        }
    }

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
    public static Transaction FrameTransaction(int seed) => FrameTransaction(seed, Eip8288Constants.LeanSphincsScheme);

    /// <summary>A frame transaction with one dependency under <paramref name="scheme"/>.</summary>
    public static Transaction FrameTransaction(int seed, byte scheme)
    {
        FrameDependency dependency = new(scheme, ValueKeccak.Compute(BitConverter.GetBytes(seed)), default);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = 1,
            SenderAddress = Address.Zero,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, dependency.VerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        transaction.Hash = new Hash256(ValueKeccak.Compute(Envelope(transaction)));
        return transaction;
    }

    /// <summary>A dependency-bearing frame transaction carrying <paramref name="blobs"/> blobs in the elided network form
    /// EIP-8437 transports: commitments bound to its versioned hashes and cell proofs, no blob data.</summary>
    public static Transaction BlobFrameTransaction(int seed, int blobs = 1, bool dependencyBearing = true)
    {
        Transaction transaction = FrameTransaction(seed);
        if (!dependencyBearing)
            transaction.Frames = [new(FrameMode.Sender, FrameFlags.None, Address.Zero, 21_000, UInt256.Zero, BitConverter.GetBytes(seed))];
        byte[][] commitments = [.. Enumerable.Range(0, blobs).Select(i => Enumerable.Repeat((byte)(seed + i + 1), 48).ToArray())];
        transaction.BlobVersionedHashes = [.. commitments.Select(static commitment =>
        {
            byte[] hash = System.Security.Cryptography.SHA256.HashData(commitment);
            hash[0] = 0x01;
            return hash;
        })];
        transaction.MaxFeePerBlobGas = 1;
        transaction.NetworkWrapper = new ShardBlobNetworkWrapper([], commitments,
            [.. Enumerable.Range(0, blobs * CkzgLib.Ckzg.CellsPerExtBlob).Select(static i => Enumerable.Repeat((byte)i, 48).ToArray())], ProofVersion.V1);
        transaction.ClearLengthCache();
        transaction.Hash = transaction.CalculateHash();
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

    /// <summary>Describes bytes that need not parse as a wrapper, as mode 1 with zero hints.</summary>
    public static WrapperAnnouncement DescribeOpaque(byte[] wrapper) => Announcement(1, wrapper.Length, ValueKeccak.Compute(wrapper));

    /// <summary>An announcement entry with arbitrary, possibly false, metadata.</summary>
    public static WrapperAnnouncement Announcement(int mode, int length, in ValueHash256 hash, WrapperCoverage coverage = default) =>
        WrapperAnnouncement.Decode(Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)mode), Eth73Rlp.EncodeUInt((ulong)length),
            Eth73Rlp.EncodeBytes(hash.Bytes), Eth73Rlp.EncodeBytes(coverage.DepsHash.Bytes), Eth73Rlp.EncodeBytes(coverage.TxsHash.Bytes)));

    /// <summary>A ProofWrappers entry pairing <paramref name="wrapper"/> with its canonical hash.</summary>
    public static ProofWrapperEntry Entry(byte[] wrapper) => new(ValueKeccak.Compute(wrapper), wrapper);
}
