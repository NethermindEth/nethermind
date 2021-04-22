using System;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Pipeline;

#nullable enable
namespace Nethermind.Dsl.Pipeline
{
    public class BlockProcessorSource<TOut> : IPipelineElement<TOut> where TOut : Block
    {
        private readonly IBlockProcessor _blockProcessor;

        public BlockProcessorSource(IBlockProcessor blockProcessor)
        {
           _blockProcessor = blockProcessor; 
        }

        public Action<TOut> Emit { private get; set; }

        public void OnBlockProcessed(object? sender, BlockProcessedEventArgs args)
        {
            if(Emit == null)
            {
                return;
            }

            Emit((TOut)args.Block);
        }
    }
}