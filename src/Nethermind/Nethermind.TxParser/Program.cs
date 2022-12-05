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
        Transaction tx = Rlp.Decode<Transaction>(Bytes.FromHexString(input));
        TxValidator txValidator = new TxValidator(ChainId.Mainnet);
        if (txValidator.IsWellFormed(tx, GrayGlacier.Instance))
        {
            EthereumEcdsa ecdsa = new(ChainId.Mainnet, SimpleConsoleLogManager.Instance);
            Address? sender = ecdsa.RecoverAddress(tx);
            if (sender == null)
            {
                throw new InvalidDataException("Could not recover sender address");
            }
            Console.WriteLine(sender);
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
