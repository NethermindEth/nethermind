// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

public class Always : IBlockValidator, ISealValidator, IUnclesValidator, ITxValidator
{
    private readonly bool _result;
    private readonly ValidationResult _validationResult;

    private Always(bool result)
    {
        _validationResult = result ? ValidationResult.Success : "Always invalid.";
        _result = result;
    }

    // ReSharper disable once NotNullMemberIsNotInitialized
    private static Always _valid;

    public static Always Valid => LazyInitializer.EnsureInitialized(ref _valid, static () => new Always(true));

    // ReSharper disable once NotNullMemberIsNotInitialized
    private static Always _invalid;

    public static Always Invalid => LazyInitializer.EnsureInitialized(ref _invalid, static () => new Always(false));

    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, out string? error) => Validate(out error);
    public bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error) => Validate(out error);
    public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false) => Validate(out _);
    public bool ValidateSeal(BlockHeader header, bool force) => Validate(out _);
    public bool Validate(BlockHeader header, BlockHeader[] uncles) => Validate(out _);
    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) => _validationResult;
    public bool ValidateWithdrawals(Block block, out string? error) => Validate(out error);
    public bool ValidateOrphanedBlock(Block block, out string? error) => Validate(out error);
    public bool ValidateSuggestedBlock(Block block, BlockHeader parent, out string? error, bool validateHashes = true) => Validate(out error);
    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, out string? error) => Validate(out error);
    public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? error) => Validate(out error);

    private bool Validate(out string? error)
    {
        error = _result ? null : "Always invalid.";
        return _result;
    }
}
