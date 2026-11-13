// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

/// <summary>Checks leanSPHINCS signatures, leanSTARK proofs and recursive proofs with the pinned native backend;
/// a native fault, ABI mismatch or missing library throws instead of returning a verdict.</summary>
// Pinned spans remain valid for each call; Rust bounds all buffers and owns returned proof allocations.
public sealed unsafe partial class NativeLeanProofVerifier : ILeanProofVerifier
{
    private const string Library = "nethermind_lean";
    private const uint ExpectedAbiVersion = 14;
    // The ABI's guests: EIP-8288's, and the one with batch pricing (ethereum/EIPs#12664).
    private const uint BaseGuest = 0;
    private const uint BatchedGuest = 1;
    private static readonly Lazy<bool> BackendAvailable = new(CheckBackend);
    public const int MaxProofBytes = Eip8288Constants.MaxProofBytes;
    public static readonly NativeLeanProofVerifier Instance = new();

    /// <summary>Checks the ABI and both recursive guest keys once, before an EIP-8288 node starts processing.</summary>
    public void EnsureAvailable() => _ = BackendAvailable.Value;

    private static bool CheckBackend()
    {
        try
        {
            // The first native call starts leanVM's worker pool on its caller; keep that on the prover thread, which dispatches
            // every proof.
            if (!ProverThread.Run(static () => AggregatedVerificationKey).AsSpan().SequenceEqual(Eip8288Constants.AggregatedVk)
                || !ProverThread.Run(static () => GuestKey(BatchedGuest)).AsSpan().SequenceEqual(Eip8288Constants.BatchedAggregatedVk))
                throw new InvalidOperationException("The native Lean recursive guest keys do not match this node.");
            Span<uint> limits = stackalloc uint[7];
            ReadOnlySpan<uint> expectedLimits = [MaxProofBytes, Eip8288Constants.MaxProofDependencies,
                Eip8288Constants.MaxInnerProofsPerStep, Eip8288Constants.LeanSphincsWitnessBytes, Eip8288Constants.MaxAggregationInputBytes,
                Eip8288Constants.MaxClaimsPerBatchLeaf, Eip8288Constants.MaxLeanStarkInstructions];
            fixed (uint* p = limits)
                if (nlean_limits(p, (nuint)limits.Length) != 1 ||
                    !limits.SequenceEqual(expectedLimits))
                    throw new InvalidOperationException("The native Lean acceptance bounds do not match this node.");
            return true;
        }
        catch (Exception exception) when (IsUnavailable(exception) || exception is InvalidOperationException)
        {
            throw new InvalidOperationException("EIP-8288 requires the pinned native Lean backend from the Nethermind.Crypto.Lean package, for linux-x64 or linux-arm64.", exception);
        }
    }

    /// <summary>Native library ABI version.</summary>
    public static uint AbiVersion => nlean_abi_version();

    /// <summary>The pinned EIP-8288 recursive guest's Fiat-Shamir verification key.</summary>
    public static byte[] AggregatedVerificationKey => GuestKey(BaseGuest);

    private static byte[] GuestKey(uint guest)
    {
        byte[] key = new byte[32];
        fixed (byte* p = key)
        {
            if (AbiVersion != ExpectedAbiVersion || nlean_aggregated_vk(guest, p) != 1) throw new InvalidOperationException("Lean verification key unavailable");
        }
        return key;
    }

    /// <summary>The native guest whose key <paramref name="aggregatedVk"/> is: the batch-pricing one for its key, else EIP-8288's,
    /// which rejects any key but its own.</summary>
    private static uint GuestOf(ReadOnlySpan<byte> aggregatedVk) =>
        aggregatedVk.SequenceEqual(Eip8288Constants.BatchedAggregatedVk) ? BatchedGuest : BaseGuest;

    /// <inheritdoc/>
    public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness)
    {
        if (witness.Length != Eip8288Constants.LeanSphincsWitnessBytes) return false;
        try
        {
            fixed (byte* d = dataHash.Bytes)
            fixed (byte* v = verificationKey.Bytes)
            fixed (byte* w = witness)
                return Verdict(AbiVersion != ExpectedAbiVersion ? -1 : nlean_verify_leansphincs(d, v, w, (nuint)witness.Length));
        }
        catch (Exception e) when (IsUnavailable(e)) { throw new InvalidOperationException("Native Lean backend unavailable", e); }
    }

    /// <inheritdoc/>
    public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness)
    {
        if (witness.IsEmpty || witness.Length > MaxProofBytes) return false;
        try
        {
            fixed (byte* d = dataHash.Bytes)
            fixed (byte* v = verificationKey.Bytes)
            fixed (byte* w = witness)
                return Verdict(AbiVersion != ExpectedAbiVersion ? -1 : nlean_verify_leanstark(d, v, w, (nuint)witness.Length));
        }
        catch (Exception e) when (IsUnavailable(e)) { throw new InvalidOperationException("Native Lean backend unavailable", e); }
    }

    /// <inheritdoc/>
    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
    {
        if (aggregatedVk.Length != 32 || proof.IsEmpty || proof.Length > Eip8288Constants.MaxAggregateProofBytes) return false;
        try
        {
            fixed (byte* h = depsHash.Bytes)
            fixed (byte* vk = aggregatedVk)
            fixed (byte* p = proof)
                return Verdict(AbiVersion != ExpectedAbiVersion ? -1 : nlean_verify_recursive(GuestOf(aggregatedVk), h, vk, (nuint)aggregatedVk.Length, p, (nuint)proof.Length));
        }
        catch (Exception e) when (IsUnavailable(e)) { throw new InvalidOperationException("Native Lean backend unavailable", e); }
    }

    /// <inheritdoc/>
    public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
    {
        try { return Prove(in depsHash, aggregatedVk, input); }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw new InvalidOperationException("Native Lean backend unavailable", exception);
        }
    }

    private static byte[] Prove(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        byte[] witness = SerializeInput(input);
        if (aggregatedVk.Length != 32 || AbiVersion != ExpectedAbiVersion) throw new InvalidOperationException("Incompatible Lean verifier ABI or key");
        ValueHash256 hash = depsHash;
        byte[] vk = aggregatedVk.ToArray();
        return ProverThread.Run(() => ProveNative(hash, vk, witness));
    }

    private static int _lastProverThreadId;

    /// <summary>The managed thread that ran the most recent native proof; a test seam.</summary>
    internal static int LastProverThreadId => Volatile.Read(ref _lastProverThreadId);

    private static byte[] ProveNative(in ValueHash256 depsHash, byte[] aggregatedVk, byte[] witness)
    {
        Volatile.Write(ref _lastProverThreadId, Environment.CurrentManagedThreadId);
        byte* proof = null;
        nuint length = 0;
        try
        {
            fixed (byte* h = depsHash.Bytes)
            fixed (byte* vk = aggregatedVk)
            fixed (byte* w = witness)
            {
                int result = nlean_prove_recursive(GuestOf(aggregatedVk), h, vk, (nuint)aggregatedVk.Length, w, (nuint)witness.Length, &proof, &length);
                if (result != 1) throw new InvalidOperationException(result == 0 ? "Lean aggregation rejected its input" : $"Lean aggregation panicked: {LastPanic()}");
            }
            if (proof == null || length > Eip8288Constants.MaxAggregateProofBytes) throw new InvalidOperationException("Invalid native Lean proof buffer");
            return new ReadOnlySpan<byte>(proof, checked((int)length)).ToArray();
        }
        finally
        {
            if (proof != null) nlean_free(proof, length);
        }
    }

    /// <summary>Runs every native proof on one long-lived thread.</summary>
    /// <remarks>The first native call starts leanVM's worker pool on its caller, and the native prover lock serializes proofs anyway.
    /// The queue is bounded, so callers that bypass the managed proving gate are refused with
    /// <see cref="LeanProverBusyException"/> rather than pile up behind the prover,
    /// and it is completed at process exit. A caller that must stop waiting runs the call on a task of its own, as
    /// <c>ProductionProofCache</c> does.</remarks>
    private static class ProverThread
    {
        private const int MaxQueued = 4;
        private static readonly BlockingCollection<Action> Queue = new(MaxQueued);
        private static readonly Thread Worker = Start();

        private static Thread Start()
        {
            Thread thread = new(() =>
            {
                foreach (Action work in Queue.GetConsumingEnumerable()) work();
            })
            { IsBackground = true, Name = "Lean prover" };
            thread.Start();
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => Queue.CompleteAdding();
            return thread;
        }

        public static byte[] Run(Func<byte[]> prove)
        {
            if (Thread.CurrentThread == Worker) return prove();
            byte[]? result = null;
            ExceptionDispatchInfo? failure = null;
            using ManualResetEventSlim done = new();
            bool queued;
            try
            {
                queued = Queue.TryAdd(() =>
                {
                    try { result = prove(); }
                    catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
                    finally { done.Set(); }
                });
            }
            catch (InvalidOperationException) { queued = false; }
            if (!queued) throw new LeanProverBusyException();
            done.Wait();
            failure?.Throw();
            return result!;
        }
    }

    internal static byte[] SerializeInput(AggregationInput input)
    {
        if (input.Deps.Count != input.Witnesses.Count || input.Deps.Count > Eip8288Constants.MaxProofDependencies || input.Discards.Count > Eip8288Constants.MaxProofDependencies
            || InnerProofs(input) > Eip8288Constants.MaxInnerProofsPerStep)
            throw new ArgumentException("Invalid Lean aggregation input", nameof(input));
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(input.Deps.Count);
        for (int i = 0; i < input.Deps.Count; i++)
        {
            WriteDependency(writer, input.Deps[i]);
            WriteBlob(writer, input.Witnesses[i].Span);
        }
        writer.Write(input.RecursiveProofs.Count);
        foreach (RecursiveProofInput child in input.RecursiveProofs)
        {
            // The local aggregate proof bound applies to private recursive inputs too, before the guest decodes them.
            if (child.InnerDeps is null || (child.InnerDeps.Count == 0) != child.Proof.IsEmpty || child.Proof.Length > Eip8288Constants.MaxAggregateProofBytes
                || child.InnerDeps.Count > Eip8288Constants.MaxProofDependencies)
                throw new ArgumentException("Invalid child dependencies", nameof(input));
            writer.Write(child.InnerDeps.Count);
            foreach (FrameDependency dep in child.InnerDeps) WriteRecord(writer, dep);
            WriteBlob(writer, child.Proof.Span);
        }
        writer.Write(input.Discards.Count);
        foreach (FrameDependency dep in input.Discards) WriteRecord(writer, dep);
        // An optional trailing section, absent when there are no batch leaves.
        if (input.BatchLeaves.Count != 0)
        {
            writer.Write(input.BatchLeaves.Count);
            foreach (BatchLeafInput leaf in input.BatchLeaves)
            {
                if (leaf.Claims.Count is 0 or > Eip8288Constants.MaxClaimsPerBatchLeaf
                    || leaf.Dependencies.Count is 0 or > Eip8288Constants.MaxProofDependencies)
                    throw new ArgumentException("Invalid batch leaf", nameof(input));
                writer.Write(leaf.VerificationKeyHash.Bytes);
                writer.Write(leaf.Claims.Count);
                foreach (ValueHash256 claim in leaf.Claims) writer.Write(claim.Bytes);
                writer.Write(leaf.Dependencies.Count);
                foreach (FrameDependency dep in leaf.Dependencies) WriteDependency(writer, dep);
                WriteBlob(writer, leaf.Proof.Span);
            }
        }
        if (stream.Length > Eip8288Constants.MaxAggregationInputBytes) throw new ArgumentException("Lean aggregation input too large", nameof(input));
        return stream.ToArray();
    }

    /// <summary>EIP-8288 inner proofs of one step: batch leaves, direct leanSTARK witnesses as singleton leaves, and recursive
    /// proofs; leanSPHINCS witnesses do not count.</summary>
    private static int InnerProofs(AggregationInput input)
    {
        int count = input.BatchLeaves.Count + input.RecursiveProofs.Count;
        foreach (FrameDependency dep in input.Deps)
            if (dep.Scheme != Eip8288Constants.LeanSphincsScheme) count++;
        return count;
    }

    /// <summary>Writes a child's or discard's 192-byte record: the dependency, then 96 zero bytes for the source only a batch claim
    /// has, which this managed fold does not carry.</summary>
    private static void WriteRecord(BinaryWriter writer, FrameDependency dep)
    {
        if (dep.Scheme == Eip8288Constants.BatchedLeanStarkScheme)
            throw new ArgumentException("A batch claim's source is not carried by the managed aggregation input");
        WriteDependency(writer, dep);
        if (writer.BaseStream.Length + Eip8288Constants.DependencyTripleLength > Eip8288Constants.MaxAggregationInputBytes) throw new ArgumentException("Lean aggregation input too large");
        writer.Write(stackalloc byte[Eip8288Constants.DependencyTripleLength]);
    }

    private static void WriteDependency(BinaryWriter writer, FrameDependency dep)
    {
        if (writer.BaseStream.Length + Eip8288Constants.DependencyTripleLength > Eip8288Constants.MaxAggregationInputBytes) throw new ArgumentException("Lean aggregation input too large");
        Span<byte> bytes = stackalloc byte[96];
        dep.WriteTo(bytes);
        writer.Write(bytes);
    }

    private static void WriteBlob(BinaryWriter writer, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        WriteBlob(writer, bytes.AsSpan());
    }

    private static void WriteBlob(BinaryWriter writer, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxProofBytes || writer.BaseStream.Length + 4 + bytes.Length > Eip8288Constants.MaxAggregationInputBytes) throw new ArgumentException("Lean aggregation input too large");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    // A fault is not a rejection: reporting it as false would mark a valid block INVALID and cache it as bad.
    private static bool Verdict(int result) => result switch
    {
        1 => true,
        0 => false,
        _ when AbiVersion != ExpectedAbiVersion => throw new InvalidOperationException("Native Lean verifier has an incompatible ABI"),
        _ => throw new InvalidOperationException($"Native Lean verifier panicked: {LastPanic()}")
    };

    /// <summary>The calling thread's last contained native panic message.</summary>
    private static string LastPanic()
    {
        Span<byte> message = stackalloc byte[512];
        nuint length;
        fixed (byte* p = message)
            length = nlean_last_error(p, (nuint)message.Length);
        return Encoding.UTF8.GetString(message[..(int)Math.Min(length, (nuint)message.Length)]);
    }

    private static bool IsUnavailable(Exception e) => e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint nlean_abi_version();
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_limits(uint* output, nuint length);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_aggregated_vk(uint guest, byte* output);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_verify_leansphincs(byte* dataHash, byte* vk, byte* witness, nuint witnessLen);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_verify_leanstark(byte* dataHash, byte* vk, byte* witness, nuint witnessLen);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_verify_recursive(uint guest, byte* depsHash, byte* vk, nuint vkLen, byte* proof, nuint proofLen);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_prove_recursive(uint guest, byte* depsHash, byte* vk, nuint vkLen, byte* input, nuint inputLen, byte** output, nuint* outputLen);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void nlean_free(byte* proof, nuint proofLen);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nuint nlean_last_error(byte* message, nuint messageLen);
}
