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
using Nethermind.Trie;

class Program
{
    static void Main(string[] args)
    {
        for (int i = 0x249f0; i <= 0x24d74; ++i)
        {
            string witnessFileName = $"{args[0]}/reth/{i.ToString("x")}.json";
            string blockFileName = $"{args[0]}/blocks/{i.ToString("x")}.json";
            Console.WriteLine($"Processing {i.ToString("X")}");
            var serializer = new EthereumJsonSerializer();

            Witness witness =
                serializer.Deserialize<Witness>(File.ReadAllText(witnessFileName));

            BlockForRpc suggestedBlockForRpc =
                serializer.Deserialize<BlockForRpc>(File.ReadAllText(blockFileName))!;

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

            BlockHeader? baseBlock = null;
            foreach (var header in witness.DecodedHeaders)
            {
                if (header.Hash == suggestedBlockHeader.ParentHash)
                {
                    baseBlock = header;
                }
            }

            if (baseBlock is null)
            {
                Console.WriteLine($"Invalid witness headers");
                continue;
            }
            Console.WriteLine($"Headers: {witness.DecodedHeaders.Length}, Base block: {baseBlock.Hash}");

            Block suggestedBlock = new Block(suggestedBlockHeader,
                suggestedBlockForRpc.Transactions.Select(tx =>
                    serializer.Deserialize<TransactionForRpc>(((JsonElement)tx).GetRawText()).ToTransaction()), [],
                suggestedBlockForRpc.Withdrawals);

            StatelessBlockProcessingEnv blockProcessingEnv =
                new(HoodiSpecProvider.Instance, Always.Valid, new NLogManager());

            IBlockProcessor blockProcessor = blockProcessingEnv.GetProcessor(witness, baseBlock.StateRoot!);

            try
            {
                Block[] processed = blockProcessor.Process(baseBlock, [suggestedBlock],
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
            catch (MissingTrieNodeException e)
            {
                Console.WriteLine($"Unable to process block: {e}");
            }
        }
    }
}
