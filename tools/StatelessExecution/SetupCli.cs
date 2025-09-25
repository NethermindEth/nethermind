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
using Nethermind.Logging;
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

        Option<string> network = new("--network")
        {
            HelpName = "Network",
            Required = true
        };
        network.AcceptOnlyFromAmong("hoodi", "mainnet");

        command.Add(witnessFile);
        command.Add(blockFile);
        command.Add(network);

        command.SetAction((parseResult, _) =>
        {
            ILogManager logManager = new SimpleConsoleLogManager(LogLevel.Info, "HH:mm:ss|");
            ILogger logger = logManager.GetClassLogger();

            string blockFileName = parseResult.GetRequiredValue(blockFile);
            string execWitnessFileName = parseResult.GetRequiredValue(witnessFile);
            string networkName = parseResult.GetRequiredValue(network);

            ISpecProvider specProvider = networkName switch
            {
                "hoodi" => HoodiSpecProvider.Instance,
                "mainnet" => MainnetSpecProvider.Instance,
                _ => throw new ArgumentException($"Unsupported network {networkName}")
            };

            logger.Info("🚀  Starting stateless execution...\n");
            logger.Info($"📁  Block file: {blockFileName}");
            logger.Info($"🔍  Witness file: {execWitnessFileName}\n");

            var serializer = new EthereumJsonSerializer();

            logger.Info("📖  Reading input files...");
            if (!File.Exists(execWitnessFileName))
            {
                logger.Info($"❌  Witness file not found: {execWitnessFileName}");
                return Task.FromResult(1);
            }
            if (!File.Exists(blockFileName))
            {
                logger.Info($"❌  Block file not found: {blockFileName}");
                return Task.FromResult(1);
            }

            var witnessBytes = File.ReadAllText(execWitnessFileName);
            var blockBytes = File.ReadAllText(blockFileName);
            logger.Info("✅  Files read successfully\n");

            logger.Info("🔍  Deserializing witness and block data...");
            Witness witness = serializer.Deserialize<Witness>(witnessBytes);
            BlockForRpc suggestedBlockForRpc = serializer.Deserialize<BlockForRpc>(blockBytes);
            logger.Info("✅  Deserialization completed\n");

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

            logger.Info("🔗  Searching for parent block in witness headers...");
            logger.Info($"    Block number: #{suggestedBlockForRpc.Number}");
            logger.Info($"    Parent hash: {suggestedBlockHeader.ParentHash}");
            logger.Info($"    Witness contains {witness.DecodedHeaders.Count} headers");

            BlockHeader? baseBlock = witness.DecodedHeaders.FirstOrDefault(h => h.Hash == suggestedBlockHeader.ParentHash);

            if (baseBlock is null)
            {
                logger.Info("❌  Decoding witness file failed - Parent block header not found.");
                logger.Info($"    Expected parent hash: {suggestedBlockHeader.ParentHash}");
                return Task.FromResult(1);
            }

            logger.Info($"✅  Found parent block #{baseBlock.Number}\n");

            logger.Info("⚙️  Initializing block processing environment...");
            logger.Info("🔄  Processing block...");
            logger.Info($"📋  Processing {suggestedBlockForRpc.Transactions.Length} transactions...");
            var transactions = new Transaction[suggestedBlockForRpc.Transactions.Length];
            for (int j = 0; j < transactions.Length; j++)
            {
                var tx = (JsonElement)suggestedBlockForRpc.Transactions[j];
                transactions[j] = serializer.Deserialize<TransactionForRpc>(tx.GetRawText()).ToTransaction();
            }

            Block suggestedBlock = new Block(suggestedBlockHeader, transactions, [], suggestedBlockForRpc.Withdrawals);

            if (!ProcessBlock(baseBlock, witness, suggestedBlock, specProvider, logManager))
            {
                return Task.FromResult(1);
            }
            return Task.FromResult(0);
        });
    }

    private static bool ProcessBlock(BlockHeader baseBlock, Witness witness, Block suggestedBlock, ISpecProvider specProvider, ILogManager logManager)
    {
        ILogger logger = logManager.GetClassLogger();
        StatelessBlockProcessingEnv blockProcessingEnv =
            new(witness, specProvider, Always.Valid, logManager);

        IBlockProcessor blockProcessor = blockProcessingEnv.BlockProcessor;

        using var scope = blockProcessingEnv.WorldState.BeginScope(baseBlock);
        try
        {
            (Block processed, TxReceipt[] _) = blockProcessor.ProcessOne(suggestedBlock,
                ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, specProvider.GetSpec(suggestedBlock.Header));

            if (processed.Hash != suggestedBlock.Hash)
            {
                logger.Info("❌  Block processing failed - Hash mismatch");
                logger.Info($"    Expected: {suggestedBlock.Hash}");
                logger.Info($"    Actual:   {processed.Hash}");
                return false;
            }
        }
        catch (MissingTrieNodeException ex)
        {
            logger.Info("❌  Decoding witness file failed - Invalid merkle proof");
            logger.Info($"    Error: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            logger.Info("❌  Block processing failed with unexpected error");
            logger.Info($"    Error: {ex.Message}");
            return false;
        }

        logger.Info("");
        logger.Info("🎉  Block processed successfully!");
        logger.Info($"    Block #{suggestedBlock.Number} validated");
        logger.Info($"    Hash: {suggestedBlock.Hash}");
        return true;
    }
}
