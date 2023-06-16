// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Cli;
using Nethermind.Cli.Console;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
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

PrivateKey privateKey = new(privateKeyString);

ILogger logger = SimpleConsoleLogManager.Instance.GetLogger("send blobs");
ICliConsole cliConsole = new CliConsole();
IJsonSerializer serializer = new EthereumJsonSerializer();
ILogManager logManager = new OneLoggerLogManager(logger);
ICliEngine engine = new CliEngine(cliConsole);
INodeManager nodeManager = new NodeManager(engine, serializer, cliConsole, logManager);
nodeManager.SwitchUri(new Uri(rpcUrl));

string? nonceString = await nodeManager.Post<string>("eth_getTransactionCount", privateKey.Address, "latest");
if(nonceString is null)
{
    logger.Error("Unable to get nonce");
    return;
}
ulong nonce = Convert.ToUInt64(nonceString, nonceString.StartsWith("0x") ? 16 : 10);

string? chainIdString = await nodeManager.Post<string>("eth_chainId") ?? "1";
ulong chainId = Convert.ToUInt64(chainIdString, chainIdString.StartsWith("0x") ? 16 : 10);

string? gasPriceRes = await nodeManager.Post<string>("eth_gasPrice") ?? "1";
UInt256 gasPrice = (UInt256)Convert.ToUInt64(gasPriceRes, gasPriceRes.StartsWith("0x") ? 16 : 10);

string? gasPriceTipRes = await nodeManager.Post<string>("eth_maxPriorityFeePerGas") ?? "1";
UInt256 gasPriceTip = (UInt256)Convert.ToUInt64(gasPriceTipRes, gasPriceTipRes.StartsWith("0x") ? 16 : 10);

Signer signer = new Signer(chainId, privateKey, new OneLoggerLogManager(logger));
TxDecoder txDecoder = new();

while (blobTxCount > 0)
{
    blobTxCount--;
    const int blobCount = 1;

    byte[][] blobs = new byte[blobCount][];
    byte[][] commitments = new byte[blobCount][];
    byte[][] proofs = new byte[blobCount][];
    byte[][] blobhashes = new byte[blobCount][];

    for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
    {
        blobs[blobIndex] = new byte[Ckzg.Ckzg.BytesPerBlob];
        new Random().NextBytes(blobs[blobIndex]);
        for (int i = 0; i < Ckzg.Ckzg.BytesPerBlob; i += 32)
        {
            blobs[blobIndex][i] = 0;
        }
        
        commitments[blobIndex] = new byte[Ckzg.Ckzg.BytesPerCommitment];
        proofs[blobIndex] = new byte[Ckzg.Ckzg.BytesPerProof];
        blobhashes[blobIndex] = new byte[32];

        KzgPolynomialCommitments.KzgifyBlob(
            blobs[blobIndex].AsSpan(),
            commitments[blobIndex].AsSpan(),
            proofs[blobIndex].AsSpan(),
            blobhashes[blobIndex].AsSpan());
    }

    Transaction tx = new()
    {
        Type = TxType.Blob,
        ChainId = chainId,
        Nonce = nonce,
        GasLimit = GasCostOf.Transaction,
        GasPrice = gasPrice,
        DecodedMaxFeePerGas = gasPrice + gasPriceTip,
        MaxFeePerDataGas = 100,
        Value = 0,
        To = new Address(receiver),
        BlobVersionedHashes = blobhashes,
        NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs),
    };

    await signer.Sign(tx);

    
    string txRlp = Hex.ToHexString(txDecoder
        .Encode(tx, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping).Bytes);

    string? result = await nodeManager.Post<string>("eth_sendRawTransaction", "0x" + txRlp);

    Console.WriteLine("Result:" + result);

    nonce++;
    //await Task.Delay(500); // delay in milliseconds
}
