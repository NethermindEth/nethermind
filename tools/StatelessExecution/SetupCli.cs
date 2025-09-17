// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System.CommandLine;
using Nethermind.Core;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Logging.NLog;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Consensus.Stateless;
using Nethermind.Core.Specs;
using Nethermind.Trie;

namespace StatelessExecution;

internal static class SetupCli
{
    public static void SetupExecute(RootCommand command)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Option<string> witnessFile = new("--witness")
        {
            Description = "Path to the witness file.",
            HelpName = "Witness",
            Required = true
        };

        Option<string> blockFile = new("--block")
        {
            Description = "Path to the block file.",
            HelpName = "Block",
            Required = true
        };

        command.Add(witnessFile);
        command.Add(blockFile);

        command.SetAction((parseResult, _) =>
        {
            string blockFileName = parseResult.GetValue(blockFile)!;
            string? execWitnessFileName = parseResult.GetValue(witnessFile)!;


            var serializer = new EthereumJsonSerializer();

            var witnessBytes = File.ReadAllText(execWitnessFileName);
            var blockBytes = File.ReadAllText(blockFileName);

            Witness witness = serializer.Deserialize<Witness>(witnessBytes);
            BlockForRpc suggestedBlockForRpc = serializer.Deserialize<BlockForRpc>(blockBytes);

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
                Hash = suggestedBlockForRpc.Hash,
            };

            // suggestedBlockHeader.Hash = suggestedBlockHeader.CalculateHash();

            BlockHeader? baseBlock = null;
            foreach (BlockHeader header in witness.DecodedHeaders)
            {
                if (header.Hash == suggestedBlockHeader.ParentHash)
                {
                    baseBlock = header;
                }
            }

            if (baseBlock is null)
            {
                // Invalid witness headers
                Console.WriteLine("\u2717 Decoding witness file failed - Parent block header not found.");
                return Task.FromResult(1);
            }

            var transactions = new Transaction[suggestedBlockForRpc.Transactions.Length];
            for (int j = 0; j < transactions.Length; j++)
            {
                var tx = (JsonElement)suggestedBlockForRpc.Transactions[j];
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
                    Console.WriteLine($"\u2717 Block processing failed");
                    Console.WriteLine($"ProcessedBlockHash:{processed.Hash} != SuggestedBlockHash{suggestedBlock.Hash}");
                    return Task.FromResult(1);
                }
            }
            catch (MissingTrieNodeException)
            {
                Console.WriteLine("\u2717 Decoding witness file failed - Invalid merkle proof");
                return Task.FromResult(1);
            }
            Console.WriteLine("\u2713 Block processed successfully!");
            return Task.FromResult(0);
        });
    }
}
