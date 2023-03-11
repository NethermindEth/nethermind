// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Core;

public static class BlockExtensions
{
    public static bool IsPoS(this Block? block) => block?.Header.IsPoS() == true;

    public static bool IsPoS(this BlockHeader? blockHeader) => blockHeader is not null && !blockHeader.IsGenesis && (blockHeader.IsPostMerge || blockHeader.Difficulty == 0);

    public static bool IsPostTTD(this BlockHeader header, ISpecProvider specProvider) => specProvider.TerminalTotalDifficulty is not null && header.TotalDifficulty >= specProvider.TerminalTotalDifficulty;

    /// <summary>
    /// Terminal PoW block: A PoW block that satisfies the following conditions pow_block.total_difficulty >= TERMINAL_TOTAL_DIFFICULTY and pow_block.parent_block.total_difficulty &lt; TERMINAL_TOTAL_DIFFICULTY
    /// </summary>
    /// <param name="header"></param>
    /// <param name="specProvider"></param>
    /// <returns></returns>
    /// <remarks>
    /// <seealso cref="https://github.com/ethereum/EIPs/blob/d896145678bd65d3eafd8749690c1b5228875c39/EIPS/eip-3675.md#specification"/>
    /// </remarks>
    public static bool IsTerminalBlock(this BlockHeader header, ISpecProvider specProvider)
    {
        bool ParentBeforeTTD()
        {
            UInt256? parentTotalDifficulty = header.TotalDifficulty >= header.Difficulty ? header.TotalDifficulty - header.Difficulty : 0;
            return parentTotalDifficulty < specProvider.TerminalTotalDifficulty;
        }

        // block is post TTD AND
        //   parent is PreTTD OR parent is Genesis
        return header.IsPostTTD(specProvider) && (header.IsGenesis || header.Difficulty != 0 && ParentBeforeTTD());
    }

    /// <summary>
    /// Terminal PoW block: A PoW block that satisfies the following conditions pow_block.total_difficulty >= TERMINAL_TOTAL_DIFFICULTY and pow_block.parent_block.total_difficulty &lt; TERMINAL_TOTAL_DIFFICULTY
    /// </summary>
    /// <param name="block"></param>
    /// <param name="specProvider"></param>
    /// <returns></returns>
    /// <remarks>
    /// <seealso cref="https://github.com/ethereum/EIPs/blob/d896145678bd65d3eafd8749690c1b5228875c39/EIPS/eip-3675.md#specification"/>
    /// </remarks>
    public static bool IsTerminalBlock(this Block block, ISpecProvider specProvider) => block.Header.IsTerminalBlock(specProvider);
}
