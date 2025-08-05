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
using Nethermind.Core.Specs;
using Nethermind.Trie;

class Program
{
    static int Main(string[] args)
    {
        for (int i = 0x249f0; i <= 0x24d74; ++i)
        {
            string witnessFileName = $"{args[0]}/reth/{i.ToString("x")}.json";
            string blockFileName = $"{args[0]}/blocks/{i.ToString("x")}.json";
            var serializer = new EthereumJsonSerializer();

            Witness witness =
                serializer.Deserialize<Witness>(witnessFileName);

            BlockForRpc suggestedBlockForRpc =
                serializer.Deserialize<BlockForRpc>(blockFileName)!;

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
                 // Invalid witness headers
                return 1;
            }

            Transaction[] transactions = new Transaction[suggestedBlockForRpc.Transactions.Length];
            for (int j = 0; j < transactions.Length; j++)
            {
                JsonElement tx = (JsonElement)suggestedBlockForRpc.Transactions[j];
                transactions[j] = serializer.Deserialize<TransactionForRpc>(tx.GetRawText()).ToTransaction();
            }
            Block suggestedBlock = new Block(suggestedBlockHeader, transactions, [], suggestedBlockForRpc.Withdrawals);

            ISpecProvider specProvider = HoodiSpecProvider.Instance;

            StatelessBlockProcessingEnv blockProcessingEnv =
                new(specProvider, Always.Valid, new NLogManager());

            IBlockProcessor blockProcessor = blockProcessingEnv.GetProcessor(witness, baseBlock.StateRoot!);

            try
            {
                (Block processed, TxReceipt[] _) = blockProcessor.ProcessOne(suggestedBlock,
                    ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, specProvider.GetSpec(suggestedBlock.Header));
                if (processed.Hash != suggestedBlock.Hash)
                {
                    // Invalid block
                    return 1;
                }
                // Block processed successfully

            }
            catch (MissingTrieNodeException)
            {
                // Invalid proof
                return 1;
            }
        }

        return 0;
    }
}
