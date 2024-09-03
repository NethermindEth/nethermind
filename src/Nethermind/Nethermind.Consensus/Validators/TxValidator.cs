// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Messages;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

public sealed class TxValidator(ulong chainId) : ITxValidator
{
    private readonly Dictionary<TxType, ITxValidator> _validators = new()
    {
        {
            TxType.Legacy, new AllTxValidator([
                IntrinsicGasTxValidator.Instance,
                new LegacySignatureTxValidator(chainId),
                ContractSizeTxValidator.Instance,
                NonBlobFieldsTxValidator.Instance,
            ])
        },
        {
            TxType.AccessList, new AllTxValidator([
                new ReleaseSpecTxValidator(spec => spec.IsEip2930Enabled),
                IntrinsicGasTxValidator.Instance,
                SignatureTxValidator.Instance,
                new ExpectedChainIdTxValidator(chainId),
                ContractSizeTxValidator.Instance,
                NonBlobFieldsTxValidator.Instance,
            ])
        },
        {
            TxType.EIP1559, new AllTxValidator([
                new ReleaseSpecTxValidator(spec => spec.IsEip1559Enabled),
                IntrinsicGasTxValidator.Instance,
                SignatureTxValidator.Instance,
                new ExpectedChainIdTxValidator(chainId),
                new GasFieldsTxValidator(),
                ContractSizeTxValidator.Instance,
                NonBlobFieldsTxValidator.Instance,
            ])
        },
        {
            TxType.Blob, new AllTxValidator([
                new ReleaseSpecTxValidator(spec => spec.IsEip4844Enabled),
                IntrinsicGasTxValidator.Instance,
                SignatureTxValidator.Instance,
                new ExpectedChainIdTxValidator(chainId),
                new GasFieldsTxValidator(),
                ContractSizeTxValidator.Instance,
                new BlobFieldsTxValidator(),
                new MempoolBlobTxValidator()
            ])
        },
    };

    public TxValidator AddValidator(TxType type, ITxValidator validator)
    {
        _validators[type] = validator;
        return this;
    }

    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) => IsWellFormed(transaction, releaseSpec, out _);

    /// <summary>
    /// Full and correct validation is only possible in the context of a specific block
    /// as we cannot generalize correctness of the transaction without knowing the EIPs implemented
    /// and the world state(account nonce in particular).
    /// Even without protocol change, the tx can become invalid if another tx
    /// from the same account with the same nonce got included on the chain.
    /// As such, we can decide whether tx is well formed as long as we also validate nonce
    /// just before the execution of the block / tx.
    /// </summary>
    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, out string? error)
    {
        if (!_validators.TryGetValue(transaction.Type, out ITxValidator? validator))
        {
            error = TxErrorMessages.InvalidTxType(releaseSpec.Name);
            return false;
        }

        return validator.IsWellFormed(transaction, releaseSpec, out error);
    }
}
