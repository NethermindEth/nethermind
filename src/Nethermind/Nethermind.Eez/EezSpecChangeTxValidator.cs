// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Eez;

/// <summary>
/// Pool and block-building admission on an EEZ L2: no system transactions and no blob transactions.
/// </summary>
/// <remarks>
/// A system transaction is unsigned, so anyone can build one; only derivation may place it in a block.
/// Block import validates it with <see cref="EezTxType.CreateValidator"/> instead, which this key never reaches.
/// </remarks>
public sealed class EezSpecChangeTxValidator : ITxValidator, ILightTxValidator, ISpecChangeTxValidator
{
    internal const string SystemTransactionNotAllowed = "EEZ system transactions cannot be submitted.";
    internal const string BlobTransactionNotAllowed = "EEZ L2 does not support blob transactions.";

    private readonly SpecChangeTxValidator _ethereumValidator;

    public EezSpecChangeTxValidator(ulong chainId)
    {
        _ethereumValidator = new SpecChangeTxValidator(chainId);
        PersistenceFingerprint = FormattableString.Invariant(
            $"1|{typeof(EezSpecChangeTxValidator).Module.ModuleVersionId:N}|{_ethereumValidator.PersistenceFingerprint}");
    }

    public string PersistenceFingerprint { get; }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        GetRejection(transaction) ?? _ethereumValidator.IsWellFormed(transaction, releaseSpec);

    public ValidationResult IsWellFormedAfterFullValidation(Transaction transaction, IReleaseSpec releaseSpec) =>
        GetRejection(transaction) ?? _ethereumValidator.IsWellFormedAfterFullValidation(transaction, releaseSpec);

    public ValidationResult IsWellFormedLight(LightTransaction transaction, IReleaseSpec releaseSpec) =>
        transaction.Type.SupportsBlobs()
            ? BlobTransactionNotAllowed
            : _ethereumValidator.IsWellFormedLight(transaction, releaseSpec);

    private static string? GetRejection(Transaction transaction) =>
        transaction.IsEezSystemTransaction() ? SystemTransactionNotAllowed
        : transaction.SupportsBlobs || transaction.CarriesBlobs ? BlobTransactionNotAllowed
        : null;
}
