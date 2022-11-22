// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

public class Always : IBlockValidator, ISealValidator, IUnclesValidator, ITxValidator
{
    private readonly bool _result;

    private Always(bool result)
    {
        _result = result;
    }

    // ReSharper disable once NotNullMemberIsNotInitialized
    private static Always _valid;

    public static Always Valid
        => LazyInitializer.EnsureInitialized(ref _valid, () => new Always(true));

    // ReSharper disable once NotNullMemberIsNotInitialized
    private static Always _invalid;

    public static Always Invalid
        => LazyInitializer.EnsureInitialized(ref _invalid, () => new Always(false));

    public bool ValidateHash(BlockHeader header)
    {
        return _result;
    }

    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle = false)
    {
        return _result;
    }

    public bool Validate(BlockHeader header, bool isUncle = false)
    {
        return _result;
    }

    public bool ValidateSuggestedBlock(Block block)
    {
        return _result;
    }

    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock)
    {
        return _result;
    }

    public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false)
    {
        return _result;
    }

    public bool ValidateSeal(BlockHeader header, bool force)
    {
        return _result;
    }

    public bool Validate(BlockHeader header, BlockHeader[] uncles)
    {
        return _result;
    }

    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
    {
        return _result;
    }

    public bool ValidateWithdrawals(Block block, out string? error)
    {
        error = null;

        return _result;
    }
}
