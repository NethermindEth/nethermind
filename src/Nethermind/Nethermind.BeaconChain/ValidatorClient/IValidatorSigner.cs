// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.ValidatorClient;

/// <summary>Signs duty objects with validator keys that this process never holds.</summary>
/// <remarks>
/// <para>
/// Each member takes the object itself, not its signing root: a signer that keeps slashing protection has to see what it signs.
/// The implementation derives the signing domain from the object's epoch and the chain's fork schedule.
/// </para>
/// <para>Thread safety: every member may be called from any thread and concurrently.</para>
/// </remarks>
public interface IValidatorSigner
{
    /// <summary>Signs an attestation under <c>DOMAIN_BEACON_ATTESTER</c> of its target epoch (phase0/validator.md "Aggregate signature").</summary>
    /// <exception cref="ValidatorSignerException">The signer refused, such as for a slashable vote or an unknown key, or could not be reached.</exception>
    ValueTask<BlsSignature> SignAttestation(BlsPublicKey publicKey, AttestationData data, CancellationToken cancellationToken);
}

/// <summary>A signature that was not produced; the duty it was for is skipped.</summary>
/// <param name="refused">Whether the signer answered and declined, as opposed to failing or being unreachable.</param>
public sealed class ValidatorSignerException(string message, bool refused, Exception? innerException) : Exception(message, innerException)
{
    public bool Refused { get; } = refused;
}
