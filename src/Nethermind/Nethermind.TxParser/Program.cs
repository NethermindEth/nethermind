using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.Crypto;
using Nethermind.Logging;

while (true)
{
    string? input = Console.ReadLine();
    if (input is null)
        break;

    try
    {
        Transaction tx = Rlp.Decode<Transaction>(Bytes.FromHexString(input), RlpBehaviors.SkipTypedWrapping);
        EthereumEcdsa ecdsa = new(ChainId.Mainnet, SimpleConsoleLogManager.Instance);
        Address? sender = ecdsa.RecoverAddress(tx);
        if (sender == null)
        {
            throw new InvalidDataException("Could not recover sender address");
        }
        Console.WriteLine(sender);
    }
    catch (Exception e)
    {
        Console.WriteLine($"err: {e.Message}");
    }
}
