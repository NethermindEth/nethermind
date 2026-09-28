// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.CommandLine;
using System.Net;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;

namespace Nethermind.Eez.Attester;

/// <summary>
/// The attester's settings. Each flag falls back to an environment variable, named as the reference signer names
/// them, so a deployment's <c>deployments.env</c> configures either signer.
/// </summary>
internal static class AttesterCommandLine
{
    public static readonly Option<string?> ListenAddress = Flag("--listen-addr", "EEZ_PROOF_SIGNER_ADDR", "Address the Prove service listens on", "127.0.0.1:50061");
    public static readonly Option<string?> ChainConfig = Flag("--chain-config", "EEZ_CHAIN_CONFIG", "L2 genesis or bare chain configuration JSON");
    public static readonly Option<string?> RollupId = Flag("--rollup-id", "EEZ_ROLLUP_ID", "Rollup id registered on L1");
    public static readonly Option<string?> VerificationKey = Flag("--vkey", "EEZ_VKEY", "Proof system verification key, 32 bytes");
    public static readonly Option<string?> AttesterAddress = Flag("--attester-address", "EEZ_ATTESTER_ADDRESS", "Address of the attestation key");
    public static readonly Option<string?> SystemAddress = Flag("--l2-system-address", "EEZ_L2_SYSTEM_ADDRESS", "Reserved L2 system address", EezConstants.SystemAddress.ToString());
    public static readonly Option<string?> ProofSystem = Flag("--proof-system", "EEZ_PROOF_SYSTEM", "ECDSAProofSystem contract the batches name");
    public static readonly Option<string?> KeyStoreDirectory = Flag("--keystore-directory", "EEZ_KEYSTORE_DIRECTORY", "Directory holding the attestation keystore", "keystore");
    public static readonly Option<string?> PasswordFile = Flag("--password-file", "EEZ_KEYSTORE_PASSWORD_FILE", "File holding the keystore password");
    public static readonly Option<string?> BlockTime = Flag("--l2-block-time-secs", "EEZ_L2_BLOCK_TIME_SECS", "L2 block time in whole seconds, as derivation uses it");
    public static readonly Option<string?> GasLimit = Flag("--l2-gas-limit", "EEZ_L2_GAS_LIMIT", "Gas limit derivation gives every L2 block", EezSettlementContext.DefaultGasLimit.ToString());
    public static readonly Option<string?> MaxBlocks = Flag("--max-request-blocks", "EEZ_PROOF_SIGNER_MAX_REQUEST_BLOCKS", "Blocks one request may span", WindowLimits.Default.MaxBlocks.ToString());
    public static readonly Option<string?> MaxBytes = Flag("--max-request-bytes", "EEZ_PROOF_SIGNER_MAX_REQUEST_BYTES", "Bytes one request may carry", WindowLimits.Default.MaxBytes.ToString());
    public static readonly Option<string?> MaxWitnessItems = Flag("--max-request-witness-items", "EEZ_PROOF_SIGNER_MAX_REQUEST_WITNESS_ITEMS", "Witness items one request may carry",
        WindowLimits.Default.MaxWitnessItems.ToString());
    public static readonly Option<string?> IdleTimeout = Flag("--stream-idle-timeout-secs", "EEZ_PROOF_SIGNER_STREAM_IDLE_TIMEOUT_SECS", "Seconds to wait for each streamed message", "120");
    public static readonly Option<string?> RequestTimeout = Flag("--request-timeout-secs", "EEZ_PROOF_SIGNER_REQUEST_TIMEOUT_SECS", "Seconds one request may take", "600");

    /// <exception cref="FormatException">A setting is missing or invalid.</exception>
    public static AttesterOptions Parse(ParseResult result)
    {
        Address systemAddress = new(Required(result, SystemAddress));
        if (systemAddress != EezConstants.SystemAddress)
        {
            throw new FormatException("native system transactions require the reserved EEZ system address");
        }

        Address proofSystem = new(Required(result, ProofSystem));
        if (proofSystem == Address.Zero)
        {
            throw new FormatException("proof-system address must be non-zero");
        }

        ValueHash256 verificationKey = new(Required(result, VerificationKey));
        if (verificationKey == default)
        {
            throw new FormatException("vkey must be non-zero");
        }

        return new AttesterOptions(
            IPEndPoint.Parse(Required(result, ListenAddress)),
            Required(result, ChainConfig),
            NonZero(result, RollupId),
            verificationKey,
            new Address(Required(result, AttesterAddress)),
            proofSystem,
            Required(result, KeyStoreDirectory),
            Required(result, PasswordFile),
            NonZero(result, BlockTime),
            NonZero(result, GasLimit),
            new WindowLimits(checked((int)NonZero(result, MaxBlocks)), checked((long)NonZero(result, MaxBytes)), checked((long)NonZero(result, MaxWitnessItems))),
            TimeSpan.FromSeconds(NonZero(result, IdleTimeout)),
            TimeSpan.FromSeconds(NonZero(result, RequestTimeout)));
    }

    public static void AddTo(Command command)
    {
        foreach (Option option in (Option[])[ListenAddress, ChainConfig, RollupId, VerificationKey, AttesterAddress, SystemAddress, ProofSystem, KeyStoreDirectory,
                     PasswordFile, BlockTime, GasLimit, MaxBlocks, MaxBytes, MaxWitnessItems, IdleTimeout, RequestTimeout])
        {
            command.Options.Add(option);
        }
    }

    public static string Required(ParseResult result, Option<string?> option) =>
        result.GetValue(option) is { } value ? value : throw new FormatException($"{option.Name} is required");

    private static ulong NonZero(ParseResult result, Option<string?> option) =>
        ulong.TryParse(Required(result, option), out ulong value) && value != 0 ? value : throw new FormatException($"{option.Name} must be a positive integer");

    private static Option<string?> Flag(string name, string environmentVariable, string description, string? fallback = null) => new(name)
    {
        Description = $"{description} (env {environmentVariable})",
        DefaultValueFactory = _ => Environment.GetEnvironmentVariable(environmentVariable) ?? fallback,
    };
}
