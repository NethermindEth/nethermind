// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Logs;

public class Importer
{
    public static void Import(string basePath, LogsBuilder builder, int range)
    {
        using var provider = new DbProvider();
        var factory = new RocksDbFactory(DbConfig.Default, LimboLogs.Instance, basePath);
        var initializer = new StandardDbInitializer(provider, factory, null);

        initializer.InitStandardDbs(true, false);

        using var defaultDb = provider.GetColumnDb<ReceiptsColumns>(DbNames.Receipts);
        using var blocks = defaultDb.GetColumnDb(ReceiptsColumns.Blocks);
        using var infos = provider.GetDb<IDb>(DbNames.BlockInfos);
        using var numbers = provider.GetDb<IDb>(DbNames.BlockNumbers);
        using var headers = provider.GetDb<IDb>(DbNames.Headers);

        var headerStore = new HeaderStore(headers, numbers);

        // from BlockTree.cs
        var stateHeadHashDbEntryAddress = new byte[16];
        var persistedNumberData = infos.Get(stateHeadHashDbEntryAddress);
        long? bestPersistedState = persistedNumberData is null ? null : new RlpStream(persistedNumberData).DecodeLong();

        var bestPersisted = infos.Get(bestPersistedState!.Value);
        var chainLevel = Rlp.GetStreamDecoder<ChainLevelInfo>()!.Decode(new RlpStream(bestPersisted!));
        var main = chainLevel.BlockInfos[0];

        var header = headerStore.Get(main.BlockHash);

        for (var i = 0; i < range; i++)
        {
            var number = header!.Number;
            var receipts = GetReceiptData(blocks, number, header.Hash!);

            if (receipts.Length != 0)
            {
                // TODO: do proper bucketing
                var no = (uint)(range - i);

                for (ushort j = 0; j < receipts.Length; j++)
                {
                    var receipt = receipts[j];

                    if (receipt.Logs == null)
                        continue;

                    foreach (var log in receipt.Logs)
                    {
                        builder.AppendRaw(log, no);
                    }
                }
            }
            else
            {
                // go back by one
                //Console.WriteLine($"No receipts for block {number} at distance {i} from the head.");
                i--;
            }

            header = headerStore.Get(header.ParentHash!);
        }
    }

    [SkipLocalsInit]
    private static unsafe TxReceipt[] GetReceiptData(IDb blocksDb, long blockNumber, Hash256 blockHash)
    {
        Span<byte> blockNumPrefixed = stackalloc byte[40];

        GetBlockNumPrefixedKey(blockNumber, blockHash, blockNumPrefixed);

        Span<byte> receiptsData = blocksDb.GetSpan(blockNumPrefixed);
        if (receiptsData.IsNull())
        {
            receiptsData = blocksDb.GetSpan(blockHash);
        }

        var decoded = ReceiptArrayStorageDecoder.Instance.Decode(receiptsData);

        blocksDb.DangerousReleaseMemory(receiptsData);

        return decoded;

        static void GetBlockNumPrefixedKey(long blockNumber, Hash256 blockHash, Span<byte> output)
        {
            blockNumber.WriteBigEndian(output);
            blockHash!.Bytes.CopyTo(output[8..]);
        }
    }
}
