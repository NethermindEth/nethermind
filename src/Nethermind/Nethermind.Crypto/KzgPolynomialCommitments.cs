// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CkzgLib;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Crypto;

public static partial class KzgPolynomialCommitments
{
    /// <summary>
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844#parameters"/>
    /// </summary>
    public static readonly UInt256 BlsModulus = new(18446744069414584321ul, 6034159408538082302ul, 3691218898639771653ul, 8353516859464449352ul);

    public const byte KzgBlobHashVersionV1 = 1;

    /// <summary>
    /// The loaded trusted setup handle.
    /// </summary>
    /// <remarks>
    /// Blocks until an in-flight <see cref="InitializeAsync"/> completes, so a caller racing node startup
    /// never observes an unloaded setup.
    /// </remarks>
    /// <exception cref="KzgSetupUnavailableException">
    /// <see cref="InitializeAsync"/> was never called, or the load failed.
    /// </exception>
    internal static nint CkzgSetup
    {
        get
        {
            nint setup = Volatile.Read(ref _ckzgSetup);
            return setup != nint.Zero ? setup : AwaitSetup();
        }
    }

    private static nint AwaitSetup()
    {
        Task initialization = Volatile.Read(ref _initializeTask)
            ?? throw new KzgSetupUnavailableException($"KZG trusted setup is not loaded; call {nameof(InitializeAsync)} first");

        try
        {
            initialization.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            throw new KzgSetupUnavailableException("KZG trusted setup failed to load", e);
        }

        return Volatile.Read(ref _ckzgSetup);
    }

    private static nint _ckzgSetup = nint.Zero;
    private static Task? _initializeTask;
    private static readonly Lock _initializeLock = new();

    public static bool IsInitialized => Volatile.Read(ref _ckzgSetup) != nint.Zero;

    public static Task InitializeAsync(ILogger logger = default, string? setupFilePath = null)
    {
        if (_initializeTask is not null)
            return _initializeTask;

        lock (_initializeLock)
        {
            _initializeTask ??= Task.Run(() => LoadTrustedSetup(logger, setupFilePath));
            return _initializeTask;
        }
    }

    private static void LoadTrustedSetup(ILogger logger, string? setupFilePath)
    {
        if (_ckzgSetup != nint.Zero)
            return;

        string trustedSetupTextFileLocation = setupFilePath ??
            Path.Combine(AppContext.BaseDirectory, "kzg_trusted_setup.txt");

        if (logger.IsInfo)
            logger.Info($"Loading {nameof(Ckzg)} trusted setup from file {trustedSetupTextFileLocation}");

        Volatile.Write(ref _ckzgSetup, Ckzg.LoadTrustedSetup(trustedSetupTextFileLocation, 8));

        if (_ckzgSetup == nint.Zero)
            throw new InvalidOperationException("Failed to load trusted setup");
    }

    /// <param name="commitment">Hash256 to calculate hash from.</param>
    /// <param name="hashBuffer">Holds the output, can safely contain any data before the call.</param>
    /// <returns><c>true</c> if succeeds; otherwise, <c>false</c>.</returns>
    public static partial bool TryComputeCommitmentHashV1(ReadOnlySpan<byte> commitment, Span<byte> hashBuffer);

    public static partial bool VerifyProof(
        ReadOnlySpan<byte> commitment,
        ReadOnlySpan<byte> z,
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> proof
    );

    public static void ComputeCellProofs(ReadOnlySpan<byte> blob, Span<byte> cellProofs)
    {
        using ArrayPoolSpan<byte> cells = new(Ckzg.CellsPerExtBlob * Ckzg.BytesPerCell);
        Ckzg.ComputeCellsAndKzgProofs(cells, cellProofs, blob, CkzgSetup);
    }

    /// <summary>
    /// Computes a single blob KZG proof for a blob and its commitment.
    /// </summary>
    /// <param name="blob">The input blob data. The span must be exactly <c>BYTES_PER_BLOB</c> bytes.</param>
    /// <param name="commitment">The KZG commitment for <paramref name="blob"/>. The span must be exactly <c>BYTES_PER_COMMITMENT</c> bytes.</param>
    /// <param name="proof">The output span where the <c>BYTES_PER_PROOF</c> proof is written.</param>
    public static void ComputeBlobProof(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> commitment, Span<byte> proof) =>
        Ckzg.ComputeBlobKzgProof(proof, blob, commitment, CkzgSetup);

    /// <param name="blob">The input blob data.</param>
    /// <param name="cells">The output span of size <c>CELLS_PER_EXT_BLOB * BYTES_PER_CELL</c> (262144 bytes) where cells will be written.</param>
    public static void ComputeCells(ReadOnlySpan<byte> blob, Span<byte> cells) => Ckzg.ComputeCells(cells, blob, CkzgSetup);
}
