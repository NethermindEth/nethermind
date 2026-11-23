// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;
using Nethermind.Logging;

namespace Nethermind.Init.Steps;

public class InitializePrecompiles(ISpecProvider specProvider, IInitConfig initConfig, ILogManager logManager, IProcessExitSource processExitSource) : IStep
{
    private static SemaphoreSlim _setupLock = new(1);
    private static bool _wasSetup = false;

    // One valid call per native library, other than c-kzg, which the KZG setup below loads; no call can be answered
    // without its library. The inputs come from the precompile test vectors, except a minimal hand-made MODEXP call.
    internal static readonly NativeLibraryProbe[] DefaultNativeLibraryProbes =
    [
        new("secp256k1", "the ECRECOVER precompile", ECRecoverPrecompile.Instance,
            Convert.FromHexString(
                "38d18acb67d25c8bb9942764b62f18e17054f66a817bd4295423adf9ed98873e000000000000000000000000000000000000000000000000000000000000001b" +
                "38d18acb67d25c8bb9942764b62f18e17054f66a817bd4295423adf9ed98873e789d1dd423d25f0772d2748d60f7e4b81bb14d086eba8e8e8efb6dcff8a4ae02"),
            ECRecoverPrecompile.Address),
        // 3 ** 2 mod 5 with one-byte operands: no shortcut answers it before GMP.
        new("GMP", "the MODEXP precompile", ModExpPrecompile.Instance,
            Convert.FromHexString(
                "00000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000001" +
                "0000000000000000000000000000000000000000000000000000000000000001030205"),
            ModExpPrecompile.Address),
        new("MCL", "the BN254 precompiles", BN254AddPrecompile.Instance,
            Convert.FromHexString(
                "089142debb13c461f61523586a60732d8b69c5b38a3380a74da7b2961d867dbf2d5fc7bbc013c16d7945f190b232eacc25da675c0eb093fe6b9f1b4b4e107b36" +
                "25f8c89ea3437f44f8fc8b6bfbb6312074dc6f983809a5e809ff4e1d076dd5850b38c7ced6e4daef9c4347f370d6d8b58f4b1d8dc61a3c59d651a0644a2a27cf"),
            BN254AddPrecompile.Address),
        // The point evaluation precompile describes a failed proof with blst.
        new("blst", "the BLS12-381 and point evaluation precompiles", Bls12381G1AddPrecompile.Instance,
            Convert.FromHexString(
                "0000000000000000000000000000000017f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb" +
                "0000000000000000000000000000000008b3f481e3aaa0f1a09e30ed741d8ae4fcf5e095d5d00af600db18cb2c04b3edd03cc744a2888ae40caa232946c5e7e1" +
                "00000000000000000000000000000000112b98340eee2777cc3c14163dea3ec97977ac3dc5c70da32e6e87578f44912e902ccef9efe28d4a78b8999dfbca9426" +
                "00000000000000000000000000000000186b28d92356c4dfec4b5201ad099dbdede3781f8998ddf929b4cd7756192185ca7b8f4ef7088f813270ac3d48868a21"),
            Bls12381G1AddPrecompile.Address, KzgPointEvaluationPrecompile.Address),
        new("secp256r1", "the P256VERIFY precompile", SecP256r1Precompile.Instance,
            Convert.FromHexString(
                "bb5a52f42f9c9261ed4361f59422a1e30036e7c32b270c8807a419feca6050232ba3a8be6b94d5ec80a6d9d1190a436effe50d85a1eee859b8cc6af9bd5c2e18" +
                "4cd60b855d442f5b3c7b11eb6c4e0ae7525fe710fab9aa7c77a67f79e6fadd762927b10512bae3eddcfe467828128bad2903269919f7086069c8c4df6c732838" +
                "c7787964eaac00e5921fb1498a60f4606766b3d9685001558d1a974e7341513e"),
            SecP256r1Precompile.Address),
    ];

    internal NativeLibraryProbe[] NativeLibraryProbes { get; init; } = DefaultNativeLibraryProbes;

    public async Task Execute(CancellationToken cancellationToken)
    {
        IReleaseSpec finalSpec = specProvider!.GetFinalSpec();
        ILogger logger = logManager.GetClassLogger<InitializePrecompiles>();

        // Before the first await, so the probes run while the steps are still being started, ahead of the EVM
        // warm-up: it exits without a message when a library is missing.
        if (!TryLoadNativeLibraries(finalSpec, logger))
            throw ExitOnMissingPrecompile();

        if (finalSpec.IsEip4844Enabled)
        {
            await _setupLock.WaitAsync(cancellationToken);
            try
            {
                if (!_wasSetup)
                {
                    await KzgPolynomialCommitments.InitializeAsync(logger, initConfig.KzgSetupPath);
                    _wasSetup = true;
                }
            }
            catch (KzgSetupUnavailableException e)
            {
                if (logger.IsError)
                    logger.Error($"Cannot start: {e.Message} Restore kzg_trusted_setup.txt from the Nethermind distribution " +
                        "or correct Init.KzgSetupPath, check file permissions, and restart the node.");
                processExitSource.Exit(ExitCodes.MissingPrecompile);
                // Cancel dependent steps without the step manager logging the failure again.
                throw new OperationCanceledException(processExitSource.Token);
            }
            catch (Exception e) when (FindNativeLoadFailure(e) is { } failure)
            {
                if (logger.IsError)
                    logger.Error(DescribeLoadFailure("c-kzg", "the point evaluation precompile", failure));
                throw ExitOnMissingPrecompile();
            }
            catch (Exception e)
            {
                if (logger.IsError)
                    logger.Error($"Couldn't initialize {nameof(KzgPolynomialCommitments)} precompile", e);
                throw;
            }
            finally
            {
                _setupLock.Release();
            }
        }
    }

    /// <summary>Runs the probe of every native library that the spec needs and logs each library that cannot be loaded.</summary>
    /// <returns><c>false</c> if at least one library cannot be loaded.</returns>
    /// <remarks>Any other exception from a probe propagates: a valid call that fails means the precompile cannot run.</remarks>
    private bool TryLoadNativeLibraries(IReleaseSpec spec, ILogger logger)
    {
        bool loaded = true;
        foreach (NativeLibraryProbe probe in NativeLibraryProbes)
        {
            if (!probe.IsNeeded(spec)) continue;

            try
            {
                _ = probe.Precompile.Run(probe.Input, spec);
            }
            catch (Exception e) when (FindNativeLoadFailure(e) is { } failure)
            {
                loaded = false;
                if (logger.IsError)
                    logger.Error(DescribeLoadFailure(probe.Library, probe.UsedBy, failure));
            }
        }

        return loaded;
    }

    private OperationCanceledException ExitOnMissingPrecompile()
    {
        processExitSource.Exit(ExitCodes.MissingPrecompile);
        // Cancel dependent steps without the step manager logging the failure again.
        return new OperationCanceledException(processExitSource.Token);
    }

    // The loader's message comes last: on Linux it lists every path that was tried, one per line.
    private static string DescribeLoadFailure(string library, string usedBy, Exception failure) =>
        $"Cannot start: the {library} native library, required by {usedBy}, cannot be loaded on {RuntimeInformation.RuntimeIdentifier}. " +
        $"Reinstall Nethermind from the package for this platform and restart the node. Details: {failure.Message}";

    /// <summary>Finds the exception, if any, saying that a native library is missing, built for another platform or lacks an export.</summary>
    private static Exception? FindNativeLoadFailure(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
                return exception;
        }

        return null;
    }

    /// <summary>A valid call of a natively backed precompile, which loads its native library the way a block would.</summary>
    /// <remarks>The probe runs when the spec enables any of <paramref name="Addresses"/>.</remarks>
    internal sealed record NativeLibraryProbe(string Library, string UsedBy, IPrecompile Precompile, byte[] Input, params Address[] Addresses)
    {
        public bool IsNeeded(IReleaseSpec spec)
        {
            foreach (Address address in Addresses)
            {
                if (spec.IsPrecompile(address))
                    return true;
            }

            return false;
        }
    }
}
