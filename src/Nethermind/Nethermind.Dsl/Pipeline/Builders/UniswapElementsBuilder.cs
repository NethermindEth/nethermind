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
// 

using System;
using System.Linq;
using Nethermind.Api;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dsl.Pipeline.Sources;

namespace Nethermind.Dsl.Pipeline.Builders
{
    public class UniswapElementsBuilder
    {
        private readonly IBlockProcessor _blockProcessor;

        public UniswapElementsBuilder(IBlockProcessor blockProcessor)
        {
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
        }

        public UniswapSource GetSourceElement(INethermindApi api)
        {
            return new(_blockProcessor, api);
        }

        public PipelineElement<UniswapData, UniswapData> GetConditionElement(string key, string operation, string value)
        {
            return operation switch
            {
                "IS" => new PipelineElement<UniswapData, UniswapData>(
                    condition: (data => data.GetType().GetProperty(key)?.GetValue(data)?.ToString()?.ToLowerInvariant() == value.ToLowerInvariant()),
                    transformData: (t => t)),
                "==" => new PipelineElement<UniswapData, UniswapData>(
                    condition: (data => data.GetType().GetProperty(key)?.GetValue(data)?.ToString()?.ToLowerInvariant() == value.ToLowerInvariant()),
                    transformData: (data => data)),
                "NOT" => new PipelineElement<UniswapData, UniswapData>(
                    condition: (data => data.GetType().GetProperty(key)?.GetValue(data)?.ToString()?.ToLowerInvariant() != value.ToLowerInvariant()),
                    transformData: (data => data)),
                "!=" => new PipelineElement<UniswapData, UniswapData>(
                    condition: (data => data.GetType().GetProperty(key)?.GetValue(data)?.ToString()?.ToLowerInvariant() != value.ToLowerInvariant()),
                    transformData: (data => data)),
                _ => null
            };
        }
    }
}