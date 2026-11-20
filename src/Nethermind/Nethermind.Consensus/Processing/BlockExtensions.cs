// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.State.Proofs;

[assembly: InternalsVisibleTo("Nethermind.Consensus.Test")]

namespace Nethermind.Consensus.Processing
{
    public static class BlockExtensions
    {
        public static bool TrySetTransactions(this Block block, Transaction[] transactions)
        {
            block.Header.TxRoot = TxTrie.CalculateRoot(transactions);

            if (block is BlockToProduce blockToProduce)
            {
                blockToProduce.Transactions = Array.ConvertAll(transactions, static tx => (MempoolTransaction)tx);
                return true;
            }

            return false;
        }

        /// <summary>Returns the transactions of <paramref name="block"/>, each with the blob sidecar it travels with.</summary>
        /// <remarks>Only a <see cref="BlockToProduce"/> holds sidecars; the transactions of any other block come without one.</remarks>
        public static IEnumerable<MempoolTransaction> GetMempoolTransactions(this Block block) =>
            block is BlockToProduce blockToProduce ? blockToProduce.Transactions : WrapTransactions(block.Transactions);

        private static IEnumerable<MempoolTransaction> WrapTransactions(Transaction[] transactions)
        {
            foreach (Transaction tx in transactions)
            {
                yield return tx;
            }
        }

        public static bool IsByNethermindNode(this Block block) => block.Header.IsByNethermindNode();

        public static bool IsByNethermindNode(this BlockHeader block) =>
            Ascii.IsValid(block.ExtraData)
            && Encoding.ASCII.GetString(block.ExtraData ?? [])
                .Contains(BlocksConfig.DefaultExtraData, StringComparison.OrdinalIgnoreCase);

        public static string ParsedExtraData(this Block block)
        {
            byte[]? data = block.ExtraData;
            if (data is null || data.Length == 0)
            {
                // If no extra data just show GasBeneficiary address
                return $"Address: {(block.Header.GasBeneficiary?.ToShortString() ?? "0x")}";
            }

            // Ideally we'd prefer to show text; so convert invalid unicode
            // and control chars to spaces and trim leading and trailing spaces.
            string extraData = new ReadOnlySpan<byte>(data).ToCleanUtf8String();

            // If the cleaned text is less than half length of input size,
            // output it as hex, else output the text.
            return extraData.Length > data.Length / 2 ?
                $"Extra Data: {extraData}" :
                $"Hex: {data.ToHexString(withZeroX: true)}";
        }
    }
}
