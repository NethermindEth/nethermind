// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.TxPool
{
    public interface ITxValidator
    {
        public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec);
        public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, ulong blockGasLimit) =>
            IsWellFormed(transaction, releaseSpec);

        public const string SpecChangeTxValidatorKey = "SpecChangeTxValidator";
    }

    /// <summary>
    /// Identifies a specification-change validator whose persisted validation result can be reused safely.
    /// </summary>
    public interface ISpecChangeTxValidator : ITxValidator
    {
        /// <summary>
        /// A process-independent fingerprint that changes whenever the validator's behavior or configuration changes.
        /// </summary>
        string PersistenceFingerprint { get; }
    }

    /// <summary>
    /// Validates only transaction fields retained by <see cref="LightTransaction"/>.
    /// </summary>
    /// <remarks>
    /// Light validation is an early rejection step and never replaces validation of the full transaction body.
    /// Implementations must not depend on fields that <see cref="LightTransaction"/> does not retain.
    /// </remarks>
    public interface ILightTxValidator
    {
        /// <summary>Validates the fields retained by a light transaction.</summary>
        ValidationResult IsWellFormedLight(LightTransaction transaction, IReleaseSpec releaseSpec);
    }
}
