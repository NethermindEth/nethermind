// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Tools.LeanBench;

public sealed record BenchCase(string Name, int SphincsCount, int StarkCount, bool SharedSignature = false)
{
    public int TransactionsPerBatch => SharedSignature ? 16 : Math.Max(1, SphincsCount + StarkCount);
}

public sealed class BatchSample
{
    public required string Case { get; init; }
    public long Sequence { get; init; }
    public int TransactionCount { get; init; }
    public int ProofBytes { get; init; }
    public int WrapperBytes { get; init; }
    public int WireBytes { get; set; }
    public int GoodputBytes { get; set; }
    public int AcceptedUniqueTransactions { get; set; }
    public int RepeatedAcceptances { get; set; }
    public string? RejectionReason { get; set; }
    public double ProveWallMs { get; set; }
    public double VerifyWallMs { get; set; }
    public double EncodeWallMs { get; set; }
    public double TransferWallMs { get; set; }
    public double AdmissionWallMs { get; set; }
    public double ProveCpuMs { get; set; }
    public double VerifyCpuMs { get; set; }
    public double NetworkCpuMs { get; set; }
    public double LatencyMs { get; set; }
    public bool ProofVerified { get; set; }
}

public sealed class BenchmarkRow
{
    public required string Kind { get; init; }
    public required string Case { get; init; }
    public int SphincsCount { get; init; }
    public int StarkCount { get; init; }
    public int TransactionsPerBatch { get; init; }
    public int MeasuredBatches { get; init; }
    public double TargetTxPerSecond { get; init; }
    public double OfferedTxPerSecond { get; init; }
    public double OfferedObjectsPerSecond { get; init; }
    public double OfferedDurationSeconds { get; init; }
    public double ActualSendDurationSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public double PreparationSeconds { get; init; }
    public double DrainSeconds => Math.Max(0, DurationSeconds - OfferedDurationSeconds);
    public long OfferedTransactions { get; init; }
    public long OfferedObjects { get; init; }
    public long DroppedTransactions { get; init; }
    public long DroppedObjects { get; init; }
    public long RejectedTransactions { get; init; }
    public long AcceptedUniqueTransactions { get; init; }
    public long RepeatedAcceptances { get; init; }
    public double AcceptedTxPerSecond => AcceptedUniqueTransactions / DurationSeconds;
    public long GoodputBytes { get; init; }
    public long PayloadBytes { get; init; }
    public long WireBytes { get; init; }
    public double GoodputMbps => 8 * GoodputBytes / DurationSeconds / 1_000_000;
    public double PayloadMbps => 8 * PayloadBytes / DurationSeconds / 1_000_000;
    public double WireMbps => 8 * WireBytes / DurationSeconds / 1_000_000;
    public double ProofBytesMean { get; init; }
    public double WrapperBytesMean { get; init; }
    public double P50LatencyMs { get; init; }
    public double P95LatencyMs { get; init; }
    public double ProveWallMs { get; init; }
    public double VerifyWallMs { get; init; }
    public double EncodeWallMs { get; init; }
    public double TransferWallMs { get; init; }
    public double AdmissionWallMs { get; init; }
    public double ProveCpuMs { get; init; }
    public double VerifyCpuMs { get; init; }
    public double NetworkCpuMs { get; init; }
    public double ProcessCpuMs { get; init; }
    public required Dictionary<string, int> RejectionReasons { get; init; }
}

/// <summary>Measures native verification invoked by the actual shared admission service.</summary>
public sealed class MeasuredVerifier : ILeanProofVerifier
{
    public Action<ValueHash256, double, double>? Verified { get; set; }
    public double VerifyWallMs { get; private set; }
    public double VerifyCpuMs { get; private set; }
    public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness)
        => NativeLeanProofVerifier.Instance.VerifyLeanSphincs(dataHash, verificationKey, witness);
    public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness)
        => NativeLeanProofVerifier.Instance.VerifyLeanStark(dataHash, verificationKey, witness);
    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
    {
        double cpu = CpuMilliseconds();
        long started = Stopwatch.GetTimestamp();
        bool verified = NativeLeanProofVerifier.Instance.VerifyRecursiveStark(depsHash, aggregatedVk, proof);
        double wall = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        double cpuMs = CpuMilliseconds() - cpu;
        VerifyWallMs += wall;
        VerifyCpuMs += cpuMs;
        if (verified) Verified?.Invoke(depsHash, wall, cpuMs);
        return verified;
    }
    public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        => NativeLeanProofVerifier.Instance.ProveRecursiveStark(depsHash, aggregatedVk, input);
    public static double CpuMilliseconds()
    {
        using Process process = Process.GetCurrentProcess();
        return process.TotalProcessorTime.TotalMilliseconds;
    }
}
