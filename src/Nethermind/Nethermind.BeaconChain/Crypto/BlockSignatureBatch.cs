// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Threading;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;
using G2 = Nethermind.Crypto.Bls.P2;

namespace Nethermind.BeaconChain.Crypto;

/// <summary>
/// The block-validity signature checks of one <c>process_block</c>, deferred and verified together
/// with <see cref="BatchSignatureVerifier.VerifyBatch"/>.
/// </summary>
/// <remarks>
/// Accepts and refuses exactly what the serial checks in <see cref="SignatureSets"/> accept and
/// refuse, and refuses with the same <see cref="BeaconStateException"/> message:
/// <list type="bullet">
/// <item>A signature the serial path cannot decode or that is outside G2 is refused at its own call site.</item>
/// <item>A public key at infinity is refused at its call site, as <c>KeyValidate</c> in the IETF draft's
/// <c>CoreVerify</c> refuses it, including an aggregate of keys that sum to infinity.</item>
/// <item>A public key outside G1 is verified serially at its call site instead of deferred.</item>
/// <item>A failed batch is attributed with <see cref="BatchSignatureVerifier.FindInvalid"/>, which
/// runs the serial primitive, so its verdict is the serial one: a set it finds invalid is refused
/// with that set's message, and no invalid set means the block stands.</item>
/// <item><see cref="Run"/> checks the batch before any other failure of the block propagates, so
/// a signature that fails ahead of that failure in spec order is still the reported one.</item>
/// </list>
/// Deposits and builder deposit requests are not deferred: their signatures decide what the
/// operation does to the state, not whether the block is valid.
/// </remarks>
public sealed class BlockSignatureBatch
{
    private readonly ISignatureVerifier _verifier;
    private readonly List<BlsSignatureSet> _sets = [];
    private readonly List<string> _failures = [];

    private readonly record struct Prepared(BlsSignature Encoded, BlsSignatureSet.ValidatedSignature Point);

    private Prepared[] _prepared = [];
    private int _nextSignature;

    internal void Predecode(int count, Func<int, BlsSignature> readSignature)
    {
        _nextSignature = 0;
        _prepared = new Prepared[count];
        BeaconParallel.For(0, count, i =>
        {
            BlsSignature encoded = readSignature(i);
            _prepared[i] = new(encoded, BlsSignatureSet.ValidatedSignature.Decode(encoded));
        });
    }

    internal BlockSignatureBatch(ISignatureVerifier verifier) => _verifier = verifier;

    internal int Count => _sets.Count;

    /// <summary>Runs <paramref name="process"/> against a new batch, then verifies every signature it deferred.</summary>
    /// <remarks>
    /// When <paramref name="process"/> fails, an invalid signature it deferred earlier is reported instead,
    /// because the serial path stops at that signature and never reaches the later failure.
    /// </remarks>
    /// <exception cref="BeaconStateException">A deferred signature is invalid; the message is the one its call site gave.</exception>
    internal static void Run(ISignatureVerifier verifier, Action<BlockSignatureBatch> process)
    {
        BlockSignatureBatch batch = new(verifier);
        try
        {
            process(batch);
        }
        catch (Exception)
        {
            batch.ThrowFirstInvalid();
            throw;
        }

        batch.Verify();
    }

    /// <summary>A signature check deferred to a batch, refused with its call site's message when the batch finds it invalid.</summary>
    public sealed class Deferral
    {
        internal Deferral(BlockSignatureBatch batch, string failure)
        {
            Batch = batch;
            Failure = failure;
        }

        internal BlockSignatureBatch Batch { get; }
        internal string Failure { get; }
    }

    /// <summary>Defers a signature check to this batch, to be refused with <paramref name="failure"/>.</summary>
    internal Deferral Defer(string failure) => new(this, failure);

    /// <summary>Verifies a signature now when <paramref name="deferral"/> is <c>null</c>, otherwise defers it.</summary>
    /// <returns><c>false</c> when the signature is refused now; <c>true</c> when it is valid or deferred.</returns>
    internal static bool Verify(ISignatureVerifier verifier, G1Affine publicKey, BlsSignature signature, Hash256 signingRoot, Deferral? deferral) =>
        !publicKey.IsInf()
        && (deferral is null
            ? verifier.Verify(publicKey, signature.Bytes, signingRoot.Bytes)
            : deferral.Batch.Add(publicKey, signature, signingRoot, deferral.Failure));

    private bool Add(G1Affine publicKey, BlsSignature signature, Hash256 signingRoot, string failure)
    {
        if (_nextSignature < _prepared.Length && _prepared[_nextSignature].Encoded == signature &&
            _prepared[_nextSignature++].Point.TryCreate(publicKey, signingRoot.Bytes, out BlsSignatureSet? prepared))
        {
            _sets.Add(prepared);
            _failures.Add(failure);
            return true;
        }
        // The serial decode, so the batch refuses exactly the encodings BlsSigner.Verify refuses.
        G2 point = new(stackalloc long[G2.Sz]);
        if (!point.TryDecode(signature.Bytes, out _))
            return false;

        if (!BlsSignatureSet.TryCreate(publicKey, point.ToAffine(), signingRoot.Bytes, out BlsSignatureSet? set))
            return _verifier.Verify(publicKey, signature.Bytes, signingRoot.Bytes);

        _sets.Add(set);
        _failures.Add(failure);
        return true;
    }

    /// <summary>Verifies every deferred signature and empties the batch.</summary>
    /// <exception cref="BeaconStateException">A deferred signature is invalid; the message is the one its call site gave.</exception>
    internal void Verify()
    {
        string? failure = FirstInvalid();
        _sets.Clear();
        _failures.Clear();
        if (failure is not null)
            throw new BeaconStateException(failure);
    }

    /// <summary>Throws for the first invalid deferred signature, if any; returns otherwise.</summary>
    /// <exception cref="BeaconStateException">A deferred signature is invalid.</exception>
    internal void ThrowFirstInvalid()
    {
        if (FirstInvalid() is { } failure)
            throw new BeaconStateException(failure);
    }

    private string? FirstInvalid()
    {
        if (_verifier.VerifyBatch(_sets))
            return null;

        int invalid = _verifier.FindInvalid(_sets);
        return invalid < 0 ? null : _failures[invalid];
    }
}
