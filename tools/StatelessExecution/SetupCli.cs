// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System.CommandLine;
using Nethermind.Core;
using System.Text.Json;
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

            Console.WriteLine("🚀  Starting stateless execution...\n");
            Console.WriteLine($"📁  Block file: {blockFileName}");
            Console.WriteLine($"🔍  Witness file: {execWitnessFileName}\n");

            var serializer = new EthereumJsonSerializer();

            Console.WriteLine("📖  Reading input files...");
            if (!File.Exists(execWitnessFileName))
            {
                Console.WriteLine($"❌  Witness file not found: {execWitnessFileName}");
                return Task.FromResult(1);
            }
            if (!File.Exists(blockFileName))
            {
                Console.WriteLine($"❌  Block file not found: {blockFileName}");
                return Task.FromResult(1);
            }

            var witnessBytes = File.ReadAllText(execWitnessFileName);
            var blockBytes = File.ReadAllText(blockFileName);
            Console.WriteLine("✅  Files read successfully\n");

            Console.WriteLine("🔍  Deserializing witness and block data...");
            Witness witness = serializer.Deserialize<Witness>(witnessBytes);
            BlockForRpc suggestedBlockForRpc = serializer.Deserialize<BlockForRpc>(blockBytes);
            Console.WriteLine("✅  Deserialization completed\n");

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

            Console.WriteLine("🔗  Searching for parent block in witness headers...");
            Console.WriteLine($"    Block number: #{suggestedBlockForRpc.Number}");
            Console.WriteLine($"    Parent hash: {suggestedBlockHeader.ParentHash}");
            Console.WriteLine($"    Witness contains {witness.DecodedHeaders.Length} headers");

            BlockHeader? baseBlock = null;
            foreach (BlockHeader header in witness.DecodedHeaders)
            {
                if (header.Hash == suggestedBlockHeader.ParentHash)
                {
                    baseBlock = header;
                    break;
                }
            }

            if (baseBlock is null)
            {
                Console.WriteLine("❌  Decoding witness file failed - Parent block header not found.");
                Console.WriteLine($"    Expected parent hash: {suggestedBlockHeader.ParentHash}");
                return Task.FromResult(1);
            }

            Console.WriteLine($"✅  Found parent block #{baseBlock.Number}\n");

            Console.WriteLine("⚙️  Initializing block processing environment...");
            Console.WriteLine("🔄  Processing block...");
            Console.WriteLine($"📋  Processing {suggestedBlockForRpc.Transactions.Length} transactions...");
            var transactions = new Transaction[suggestedBlockForRpc.Transactions.Length];
            for (int j = 0; j < transactions.Length; j++)
            {
                var tx = (JsonElement)suggestedBlockForRpc.Transactions[j];
                transactions[j] = serializer.Deserialize<TransactionForRpc>(tx.GetRawText()).ToTransaction();
            }

            Block suggestedBlock = new Block(suggestedBlockHeader, transactions, [], suggestedBlockForRpc.Withdrawals);

            ISpecProvider specProvider = HoodiSpecProvider.Instance;

            StatelessBlockProcessingEnv blockProcessingEnv =
                new(witness, specProvider, Always.Valid, new NLogManager());

            IBlockProcessor blockProcessor = blockProcessingEnv.BlockProcessor;

            using var scope = blockProcessingEnv.WorldState.BeginScope(baseBlock);

            try
            {
                (Block processed, TxReceipt[] _) = blockProcessor.ProcessOne(suggestedBlock,
                    ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, specProvider.GetSpec(suggestedBlock.Header));

                if (processed.Hash != suggestedBlock.Hash)
                {
                    Console.WriteLine("❌  Block processing failed - Hash mismatch");
                    Console.WriteLine($"    Expected: {suggestedBlock.Hash}");
                    Console.WriteLine($"    Actual:   {processed.Hash}");
                    return Task.FromResult(1);
                }
            }
            catch (MissingTrieNodeException ex)
            {
                Console.WriteLine("❌  Decoding witness file failed - Invalid merkle proof");
                Console.WriteLine($"    Error: {ex.Message}");
                return Task.FromResult(1);
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌  Block processing failed with unexpected error");
                Console.WriteLine($"    Error: {ex.Message}");
                return Task.FromResult(1);
            }

            Console.WriteLine();
            Console.WriteLine("🎉  Block processed successfully!");
            Console.WriteLine($"    Block #{suggestedBlockForRpc.Number} validated");
            Console.WriteLine($"    Hash: {suggestedBlock.Hash}");
            return Task.FromResult(0);
        });
    }
}
