// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

/// <summary>Checks leanSPHINCS signature claims and recursive proofs with the pinned native backend;
/// a native fault, ABI mismatch or missing library throws instead of returning a verdict.</summary>
// Pinned spans remain valid for each call; Rust bounds all buffers and owns returned proof allocations.
public sealed unsafe partial class NativeLeanProofVerifier : ILeanProofVerifier
{
    private const string Library = "nethermind_lean";
    private const uint ExpectedAbiVersion = 8;
    private const int MaxRecursiveInputs = 16;
    private static readonly Lazy<bool> BackendAvailable = new(CheckBackend);
    public const int MaxProofBytes = Eip8288Constants.MaxProofBytes;
    public static readonly NativeLeanProofVerifier Instance = new();

    /// <summary>Checks the ABI and recursive guest key once, before an EIP-8288 node starts processing.</summary>
    public void EnsureAvailable() => _ = BackendAvailable.Value;

    private static bool CheckBackend()
    {
        try
        {
            if (!AggregatedVerificationKey.AsSpan().SequenceEqual(Eip8288Constants.AggregatedVk))
                throw new InvalidOperationException("The native Lean recursive guest key does not match this node.");
            Span<uint> limits = stackalloc uint[6];
            ReadOnlySpan<uint> expectedLimits = [MaxProofBytes, Eip8288Constants.MaxProofDependencies,
                MaxRecursiveInputs, Eip8288Constants.LeanSphincsWitnessBytes, Eip8288Constants.MaxAggregationInputBytes, Eip8288Constants.MaxMixedGuestProofBytes];
            fixed (uint* p = limits)
                if (nlean_limits(p, (nuint)limits.Length) != 1 ||
                    !limits.SequenceEqual(expectedLimits))
                    throw new InvalidOperationException("The native Lean acceptance bounds do not match this node.");
            return true;
        }
        catch (Exception exception) when (IsUnavailable(exception) || exception is InvalidOperationException)
        {
            throw new InvalidOperationException("EIP-8288 requires the pinned native Lean backend. Build with BuildLeanFfi=true and install the host library beside Nethermind.", exception);
        }
    }

    /// <summary>Native library ABI version.</summary>
    public static uint AbiVersion => nlean_abi_version();

    /// <summary>The pinned recursive guest's Fiat-Shamir verification key.</summary>
    public static byte[] AggregatedVerificationKey
    {
        get
        {
            byte[] key = new byte[32];
            fixed (byte* p = key)
            {
                if (AbiVersion != ExpectedAbiVersion || nlean_aggregated_vk(p) != 1) throw new InvalidOperationException("Lean verification key unavailable");
            }
            return key;
        }
    }

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
    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
    {
        if (aggregatedVk.Length != 32 || proof.IsEmpty || proof.Length > Eip8288Constants.MaxAggregateProofBytes) return false;
        try
        {
            fixed (byte* h = depsHash.Bytes)
            fixed (byte* vk = aggregatedVk)
            fixed (byte* p = proof)
                return Verdict(AbiVersion != ExpectedAbiVersion ? -1 : nlean_verify_recursive(h, vk, (nuint)aggregatedVk.Length, p, (nuint)proof.Length));
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
                if (nlean_prove_recursive(h, vk, (nuint)aggregatedVk.Length, w, (nuint)witness.Length, &proof, &length) != 1)
                    throw new InvalidOperationException("Lean aggregation failed");
            if (proof == null || length > Eip8288Constants.MaxAggregateProofBytes) throw new InvalidOperationException("Invalid native Lean proof buffer");
            return new ReadOnlySpan<byte>(proof, checked((int)length)).ToArray();
        }
        finally
        {
            if (proof != null) nlean_free(proof, length);
        }
    }

    /// <summary>Runs every native proof on one long-lived thread.</summary>
    /// <remarks>leanVM's proving arena gives each thread that allocates during a proof its own slab and never returns a slab's
    /// touched pages, so proofs called from changing thread-pool threads each leave another proof's peak resident (about
    /// 1.5 GB for four witnesses) until the process runs out of memory. The native prover lock serializes proofs anyway.
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
        if (input.Deps.Count != input.Witnesses.Count || input.Deps.Count > Eip8288Constants.MaxProofDependencies || input.RecursiveProofs.Count > MaxRecursiveInputs || input.Discards.Count > Eip8288Constants.MaxProofDependencies)
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
            foreach (FrameDependency dep in child.InnerDeps) WriteDependency(writer, dep);
            WriteBlob(writer, child.Proof.Span);
        }
        writer.Write(input.Discards.Count);
        foreach (FrameDependency dep in input.Discards) WriteDependency(writer, dep);
        if (stream.Length > Eip8288Constants.MaxAggregationInputBytes) throw new ArgumentException("Lean aggregation input too large", nameof(input));
        return stream.ToArray();
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
        _ => throw new InvalidOperationException("Native Lean verifier failed or has an incompatible ABI")
    };

    private static bool IsUnavailable(Exception e) => e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint nlean_abi_version();
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_limits(uint* output, nuint length);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_aggregated_vk(byte* output);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_verify_leansphincs(byte* dataHash, byte* vk, byte* witness, nuint witnessLen);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_verify_recursive(byte* depsHash, byte* vk, nuint vkLen, byte* proof, nuint proofLen);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_prove_recursive(byte* depsHash, byte* vk, nuint vkLen, byte* input, nuint inputLen, byte** output, nuint* outputLen);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void nlean_free(byte* proof, nuint proofLen);
}
