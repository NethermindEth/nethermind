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
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NSubstitute;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

/// <summary>Accepts every proof, optionally blocking or rejecting recursive STARK checks.</summary>
internal sealed class LeanTestVerifier : ILeanProofVerifier
{
    public bool Valid { get; set; } = true;
    public ManualResetEventSlim? Gate { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void EnsureAvailable() { }
    public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => Valid;
    public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => Valid;

    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
    {
        Entered.TrySetResult();
        Gate?.Wait(TimeSpan.FromSeconds(10));
        return Valid;
    }

    public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input) => [1];
}

/// <summary>Records what the transport sends to one peer.</summary>
internal sealed class FakeLink(string name = "peer") : ILeanLink
{
    private readonly List<LeanMessage> _sent = [];
    private readonly List<ChunkMessage> _chunks = [];
    private readonly List<string> _penalties = [];

    public Func<ChunkMessage, CancellationToken, ValueTask<bool>>? OnChunk { get; set; }
    public string Description => name;

    public void Send(LeanMessage message) { lock (_sent) _sent.Add(message); }

    public async ValueTask<bool> SendChunkAsync(ChunkMessage message, CancellationToken cancellationToken)
    {
        if (OnChunk is { } onChunk && !await onChunk(message, cancellationToken)) return false;
        lock (_chunks) _chunks.Add(message);
        return true;
    }

    public void Penalize(string reason) { lock (_penalties) _penalties.Add(reason); }

    public T[] Sent<T>() where T : LeanMessage { lock (_sent) return [.. _sent.OfType<T>()]; }
    public ChunkMessage[] Chunks { get { lock (_chunks) return [.. _chunks]; } }
    public string[] Penalties { get { lock (_penalties) return [.. _penalties]; } }
    public void Clear() { lock (_sent) _sent.Clear(); }
}

/// <summary>A transport over substitute chain components, with valid-by-default proofs and a manual clock.</summary>
internal sealed class LeanTestNode : IDisposable
{
    public static readonly Hash256 Genesis = TestItem.KeccakH;

    public LeanTestNode(bool manualTime = true, IReleaseSpec? spec = null)
    {
        Time = manualTime ? new ManualTimeProvider() : null;
        Specs = new TestSingleReleaseSpecProvider(spec ?? Eip8288Prototype.Instance);
        Tree.ChainId.Returns(1UL);
        Tree.Genesis.Returns(Build.A.BlockHeader.WithHash(Genesis).TestObject);
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
            if (Reject) return AcceptTxResult.Invalid;
            lock (Pending) Pending.Add(call.Arg<Transaction>());
            return AcceptTxResult.Accepted;
        });
        Wrappers = new ProofWrapperService(Pool, Specs, Tree, ProofStore, Verifier);
        Transport = new LeanObjectTransport(Tree, Specs, Pool, Wrappers, LimboLogs.Instance, null, Time);
    }

    public ManualTimeProvider? Time { get; }
    public IBlockTree Tree { get; } = Substitute.For<IBlockTree>();
    public ITxPool Pool { get; } = Substitute.For<ITxPool>();
    public TestSingleReleaseSpecProvider Specs { get; }
    public LeanTestVerifier Verifier { get; } = new();
    public List<Transaction> Pending { get; } = [];
    public LeanProofStore ProofStore { get; } = new();
    public bool Reject { get; set; }
    public ProofWrapperService Wrappers { get; }
    public LeanObjectTransport Transport { get; }

    public static LeanStatusMessage Status(ulong maxObjectBytes = LeanLimits.MaxObjectBytes, byte kinds = LeanObjectTransport.LocalKinds) =>
        new(1, Genesis.ValueHash256, [LeanObjectTransport.LocalProfile], kinds, maxObjectBytes);

    public LeanPeer Connect(FakeLink link, LeanStatusMessage? status = null) =>
        Transport.Accept(link, status ?? Status()) ?? throw new InvalidOperationException("Status was refused");

    public void Tick(TimeSpan elapsed) => Time!.AdvanceAndFireTimer(elapsed);

    public void Dispose() => Transport.Dispose();
}

internal static class LeanTestObjects
{
    /// <summary>A frame transaction that passes admission with one SPHINCS dependency.</summary>
    public static Transaction FrameTransaction(int seed)
    {
        FrameDependency dependency = Dependency(seed);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = 1,
            SenderAddress = Address.Zero,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        // A receiver recomputes the hash from the envelope.
        transaction.Hash = new Hash256(ValueKeccak.Compute(Envelope(transaction)));
        return transaction;
    }

    public static FrameDependency Dependency(int seed) => new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute([(byte)seed]), default);

    public static byte[] Envelope(Transaction transaction) =>
        TxDecoder.Instance.Encode(transaction, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping).Bytes;

    /// <summary>A mode-1 wrapper whose opaque recursive proof spans several chunks.</summary>
    public static byte[] Wrapper(int proofBytes, bool hashOnly = false, params Transaction[] transactions)
    {
        Transaction[] sorted = [.. transactions.OrderBy(t => t.Hash!.ToString())];
        List<FrameDependency> deps = Eip8288Dependencies.Canonicalize(sorted.SelectMany(Eip8288Dependencies.ForTransaction));
        return MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [.. sorted.Select(t => hashOnly ? new WrapperTransaction(t.Hash!) : new WrapperTransaction(t))],
            Deps = deps,
            Mode = MempoolWrapper.ModeRecursive,
            RecursiveStark = new RecursiveStark(Proof(proofBytes), new Hash256(Eip8288Dependencies.ComputeDepsHash(deps)))
        }).Bytes;
    }

    public static byte[] InclusionList(int proofBytes, params Transaction[] transactions)
    {
        List<FrameDependency> deps = Eip8288Dependencies.Canonicalize(transactions.SelectMany(Eip8288Dependencies.ForTransaction));
        return InclusionListProofPackageDecoder.Instance.Encode(new InclusionListProofPackage
        {
            Transactions = transactions,
            RecursiveStark = new RecursiveStark(Proof(proofBytes), new Hash256(Eip8288Dependencies.ComputeDepsHash(deps)))
        }).Bytes;
    }

    public static byte[] Proof(int bytes)
    {
        byte[] proof = new byte[bytes];
        new Random(bytes).NextBytes(proof);
        return proof;
    }

    /// <summary>A canonical kind-1 body of roughly <paramref name="size"/> bytes that is not a valid wrapper.</summary>
    public static byte[] OpaqueWrapperBody(int size, int seed = 0)
    {
        byte[] envelope = new byte[size];
        new Random(seed).NextBytes(envelope);
        return LeanRlp.EncodeList(LeanRlp.EncodeList(LeanRlp.EncodeList(LeanRlp.EncodeUInt(0), LeanRlp.EncodeBytes(envelope))),
            LeanRlp.EncodeUInt(1), LeanRlp.EncodeList(LeanRlp.EncodeList(), LeanRlp.EncodeBytes([])));
    }

    public static LeanDescriptor Describe(byte[] body, out LeanChunkTree tree) =>
        LeanDescriptor.Create(LeanProtocol.KindWrapper, LeanObjectTransport.LocalProfile, LeanDescriptor.WrapperContext(), body, out tree);

    public static ChunkMessage Chunk(ulong requestId, LeanDescriptor descriptor, byte[] body, LeanChunkTree tree, int index) =>
        new(requestId, descriptor.ObjectId, index, body.AsMemory(index * LeanProtocol.ChunkBytes, descriptor.ChunkLength(index)), tree.GetBranch(index));

    public static byte[] Wire(ChunkMessage chunk) => new ChunkMessageSerializer().Encode(chunk);
}
