// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Consensus.Validators;
using Nethermind.Specs.Forks;

while (true)
{
    string? input = Console.ReadLine();
    if (input is null)
        break;

    try
    {
        Transaction tx = Rlp.Decode<Transaction>(Bytes.FromHexString(input), RlpBehaviors.SkipTypedWrapping);
        TxValidator txValidator = new TxValidator(BlockchainIds.Mainnet);
        if (txValidator.IsWellFormed(tx, GrayGlacier.Instance))
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Mainnet, SimpleConsoleLogManager.Instance);
            Address? sender = ecdsa.RecoverAddress(tx);
            if (sender == null)
            {
                throw new InvalidDataException("Could not recover sender address");
            }
            Console.WriteLine(string.Concat(sender, " ", tx.Type));
        }
        else
        {
            throw new InvalidDataException("Transaction is not well formed");
        }
    }
    catch (Exception e)
    {
        Console.WriteLine($"err: {e.Message}");
    }
}
