//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Core;

namespace Nethermind.Blockchain.Processing
{
    public interface IBlockPreprocessorStep
    {
        /// <summary>
        /// Called before the block is put into the processing queue. Example would be recovering transaction
        /// sender addresses for each transaction.
        /// RECOVERY QUEUE - BLOCK N - BLOCK (N+1) - BLOCK (N+2) - ...
        /// RecoverData
        /// PROCESSING QUEUE - BLOCK (N-2) - BLOCK (N-1) - ...
        /// ProcessBlock
        /// </summary>
        /// <param name="block">Block to change / enrich before processing.</param>
        void RecoverData(Block block);
    }
}
