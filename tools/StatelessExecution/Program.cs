// See https://aka.ms/new-console-template for more information

using System.Text.Json;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Logging.NLog;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Consensus.Stateless;

class Program
{
    static void Main(string[] args)
    {
        var serializer = new EthereumJsonSerializer();

        Witness witness =
            serializer.Deserialize<Witness>(File.ReadAllText(args[0]));

        BlockForRpc suggestedBlockForRpc =
            serializer.Deserialize<BlockForRpc>(File.ReadAllText(args[1]))!;

        BlockHeader suggestedBlockHeader = new BlockHeader(
            suggestedBlockForRpc.ParentHash,
            suggestedBlockForRpc.Sha3Uncles,
            suggestedBlockForRpc.Miner,
            suggestedBlockForRpc.Difficulty,
            suggestedBlockForRpc.Number!.Value,
            suggestedBlockForRpc.GasLimit,
            (ulong)suggestedBlockForRpc.Timestamp,
            suggestedBlockForRpc.ExtraData,
            suggestedBlockForRpc.BlobGasUsed,
            suggestedBlockForRpc.ExcessBlobGas,
            suggestedBlockForRpc.ParentBeaconBlockRoot,
            suggestedBlockForRpc.RequestsHash)
        {
            StateRoot = suggestedBlockForRpc.StateRoot,
            TxRoot = suggestedBlockForRpc.TransactionsRoot,
            ReceiptsRoot = suggestedBlockForRpc.ReceiptsRoot,
            Bloom = suggestedBlockForRpc.LogsBloom,
            GasUsed = suggestedBlockForRpc.GasUsed,
            MixHash = suggestedBlockForRpc.MixHash,
            BaseFeePerGas = suggestedBlockForRpc.BaseFeePerGas!.Value,
            WithdrawalsRoot = suggestedBlockForRpc.WithdrawalsRoot,
            ParentBeaconBlockRoot = suggestedBlockForRpc.ParentBeaconBlockRoot,
            RequestsHash = suggestedBlockForRpc.RequestsHash,
            BlobGasUsed = suggestedBlockForRpc.BlobGasUsed,
            ExcessBlobGas = suggestedBlockForRpc.ExcessBlobGas,
        };

        suggestedBlockHeader.Hash = suggestedBlockHeader.CalculateHash();

        if (witness.Headers[0].Hash != suggestedBlockHeader.ParentHash)
        {
            Console.WriteLine("Invalid witness headers");
            return;
        }

        Block suggestedBlock = new Block(suggestedBlockHeader,
            suggestedBlockForRpc.Transactions.Select(tx =>
                serializer.Deserialize<TransactionForRpc>(((JsonElement)tx).GetRawText()).ToTransaction()), [],
            suggestedBlockForRpc.Withdrawals);

        StatelessBlockProcessingEnv blockProcessingEnv =
            new(HoodiSpecProvider.Instance, Always.Valid, new NLogManager());

        IBlockProcessor blockProcessor = blockProcessingEnv.GetProcessor(witness);

        Block[] processed = blockProcessor.Process(witness.Headers[0], [suggestedBlock],
            ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance);

        if (processed[0].Hash != suggestedBlock.Hash)
        {
            Console.WriteLine($"Invalid block. Expected {suggestedBlock.Hash}, but got {processed[0].Hash}");
        }
        else
        {
            Console.WriteLine("Block processed successfully");
        }
    }
}
