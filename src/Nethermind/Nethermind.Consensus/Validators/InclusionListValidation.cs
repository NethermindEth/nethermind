// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.Validators;

public readonly struct InclusionListValidation
{
    private readonly IBlockValidator? _validator;
    private readonly IReadOnlyDictionary<AddressAsKey, AccountSnapshot>? _parentSenderState;

    public static InclusionListValidation NoOp => default;

    internal InclusionListValidation(IBlockValidator validator, IReadOnlyDictionary<AddressAsKey, AccountSnapshot> parentSenderState)
    {
        _validator = validator;
        _parentSenderState = parentSenderState;
    }

    public void Commit(Block processedBlock, Block suggestedBlock)
    {
        if (_validator is null) return;
        processedBlock.InclusionListTransactions = suggestedBlock.InclusionListTransactions;
        suggestedBlock.InclusionListUnsatisfied = !_validator.ValidateInclusionList(processedBlock, _parentSenderState!);
    }
}
