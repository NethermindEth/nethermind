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

IReleaseSpec spec = Fork.GetLatest();
const ulong chainId = BlockchainIds.Mainnet;
LegacySignatureTxValidator legacySignatureTxValidator = new(chainId);
SignatureTxValidator signatureTxValidator = SignatureTxValidator.Instance;
EthereumEcdsa ecdsa = new(chainId);
ExpectedChainIdTxValidator expectedChainIdTxValidator = new(chainId);

while (true)
{
    string? input = Console.ReadLine();
    if (input is null)
        break;

    try
    {
        byte[] bytes = Bytes.FromHexString(input);
        Transaction tx = Rlp.Decode<Transaction>(bytes, RlpBehaviors.SkipTypedWrapping);
        ITxValidator signatureValidator = tx.Type == TxType.Legacy
            ? legacySignatureTxValidator
            : signatureTxValidator;

        ValidationResult signatureValidation = signatureValidator.IsWellFormed(tx, spec);
        if (!signatureValidation)
        {
            Console.WriteLine($"err: {signatureValidation.Error}");
            continue;
        }

        if (tx.Type != TxType.Legacy)
        {
            signatureValidation = expectedChainIdTxValidator.IsWellFormed(tx, spec);
            if (!signatureValidation)
            {
                Console.WriteLine($"err: {signatureValidation.Error}");
                continue;
            }
        }

        Address? sender = ecdsa.RecoverAddress(tx, !spec.ValidateChainId);
        if (sender is null)
        {
            Console.WriteLine($"err: Could not recover sender address");
            continue;
        }

        Console.WriteLine($"{sender} {tx.Type}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"err: {e.Message.Replace(Environment.NewLine, ". ").Replace("\n", ". ")}");
    }
}
