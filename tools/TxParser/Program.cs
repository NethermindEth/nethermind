// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.Crypto;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;

while (true)
{
    string? input = Console.ReadLine();
    if (input is null)
        break;

    try
    {
        IReleaseSpec spec = Fork.GetLatest();
        byte[] bytes = Bytes.FromHexString(input);
        Transaction tx = Rlp.Decode<Transaction>(bytes, RlpBehaviors.SkipTypedWrapping);
        const ulong chainId = BlockchainIds.Mainnet;

        ITxValidator signatureValidator = tx.Type == TxType.Legacy
            ? new LegacySignatureTxValidator(chainId)
            : SignatureTxValidator.Instance;

        ValidationResult signatureValidation = signatureValidator.IsWellFormed(tx, spec);
        if (!signatureValidation)
        {
            throw new InvalidDataException($"{signatureValidation.Error}");
        }

        EthereumEcdsa ecdsa = new(chainId);
        Address? sender = ecdsa.RecoverAddress(tx, !spec.ValidateChainId);
        if (sender is null)
        {
            throw new InvalidDataException("Could not recover sender address");
        }

        Console.WriteLine($"{sender} {tx.Type}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"err: {e.Message.Replace(Environment.NewLine, ". ").Replace("\n", ". ")}");
    }
}
