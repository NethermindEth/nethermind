using Nethermind.Cli;
using Nethermind.Cli.Console;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Org.BouncyCastle.Utilities.Encoders;

// send-blobs <url-without-auth> <transactions-count-1-blob-each> <secret-key> <receiver-address>
// send-blobs http://localhost:8545 5 0x0000000000000000000000000000000000000000000000000000000000000000 0x000000000000000000000000000000000000f1c1

string rpcUrl = args[0];
int blobTxCount = int.Parse(args[1]);
string privateKeyString = args[2];
string receiver = args[3];


await KzgPolynomialCommitments.InitializeAsync();

ulong inc = 0;

PrivateKey privateKey = new(privateKeyString);

ILogger logger = LimboLogs.Instance.GetLogger("send blobs");
ICliConsole cliConsole = new CliConsole();
IJsonSerializer serializer = new EthereumJsonSerializer();
ILogManager logManager = new OneLoggerLogManager(logger);
ICliEngine engine = new CliEngine(cliConsole);
INodeManager nodeManager = new NodeManager(engine, serializer, cliConsole, logManager);
string nonceString = await nodeManager.Post<string>("eth_getTransactionCount", privateKey.Address, "latest") ?? "0";
//nodeManager.SwitchUri(new Uri("https://rpc.bootnode-1.srv.4844-devnet-5.ethpandaops.io"));
nodeManager.SwitchUri(new Uri(rpcUrl));
ulong nonce = Convert.ToUInt64(nonceString, nonceString.StartsWith("0x") ? 16 : 10);
string? chainIdString = await nodeManager.Post<string>("eth_chainId") ?? "1";
ulong chainId = Convert.ToUInt64(chainIdString, chainIdString.StartsWith("0x") ? 16 : 10);

while (blobTxCount > 0)
{
    blobTxCount--;
    nonce++;
    inc++;
    byte[][] blobs = new byte[1][];
    blobs[0] = new byte[Ckzg.Ckzg.BytesPerBlob];
    new Random().NextBytes(blobs[0]);
    for (int i = 0; i < Ckzg.Ckzg.BytesPerBlob; i += 32)
    {
        blobs[0][i] = 0;
    }

    byte[][] commitments = new byte[1][];
    commitments[0] = new byte[Ckzg.Ckzg.BytesPerCommitment];
    byte[][] proofs = new byte[1][];
    proofs[0] = new byte[Ckzg.Ckzg.BytesPerProof];
    byte[] blobhash = new byte[32];

    KzgPolynomialCommitments.KzgifyBlob(blobs[0].AsSpan(), commitments[0].AsSpan(), proofs[0].AsSpan(), blobhash.AsSpan());

    Transaction tx = new()
    {
        Type = TxType.Blob,
        ChainId = chainId,
        Nonce = nonce,
        GasLimit = 210000,
        GasPrice = 5000000000 + inc * 10000,
        DecodedMaxFeePerGas = 5000000000 + inc * 10000,
        MaxFeePerDataGas = 5000000000, // needs to be at least the min fee
        Value = 0,
        To = new Address(receiver),
        BlobVersionedHashes = new[] { blobhash },
        NetworkWrapper = new ShardBlobNetworkWrapper(commitments, blobs, proofs),
    };

    await new Signer(chainId, privateKey, new OneLoggerLogManager(logger)).Sign(tx);

    TxDecoder txDecoder = new();
    string txRlp = Hex.ToHexString(txDecoder
        .Encode(tx, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping).Bytes);


    //{
    //    // verify
    //    Transaction? tx2 = txDecoder.Decode(new RlpStream(Hex.Decode(txRlp)),
    //        RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping);
    //    var wrapper = tx2.NetworkWrapper as ShardBlobNetworkWrapper;
    //    bool check = KzgPolynomialCommitments.AreProofsValid(wrapper?.Blobs!,
    //        wrapper?.Commitments!, wrapper?.Proofs!);

    //    if (!check)
    //    {
    //        throw new InvalidProgramException();
    //    }

    //    byte[] hash = new byte[32];
    //    byte[][] commitements = wrapper.Commitments;
    //    for (int i = 0, n = 0;
    //         i < tx.BlobVersionedHashes!.Length;
    //         i++, n += Ckzg.Ckzg.BytesPerCommitment)
    //    {
    //        if (!KzgPolynomialCommitments.TryComputeCommitmentHashV1(
    //                commitements[0], hash) ||
    //            !hash.SequenceEqual(tx.BlobVersionedHashes[i]!))
    //        {
    //            Console.WriteLine("Commitment to hash is incorrect: {0} vs {1}", Hex.ToHexString(hash.ToArray()),
    //                Hex.ToHexString(tx.BlobVersionedHashes[i]));
    //            return;
    //        }
    //    }
    //}

    // while (true)
    // {
    //     ulong blockNo = Convert.ToUInt64(await nodeManager.Post<string>("eth_blockNumber") ?? "0x0", 16);
    //     Console.WriteLine("The current block is {0}", blockNo);
    //     if (blockNo > 32 * 2)
    //     {
    //         break;
    //     }
    //
    //     await Task.Delay(2000);
    // }

    string? result = await nodeManager.Post<string>("eth_sendRawTransaction", "0x" + txRlp);

    Console.WriteLine("Result:" + result);
    Console.WriteLine("Done");

    //await Task.Delay(5_000);
}
