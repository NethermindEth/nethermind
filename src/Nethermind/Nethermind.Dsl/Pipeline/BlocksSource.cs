using System;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Pipeline;

#nullable enable
namespace Nethermind.Dsl.Pipeline
{
    public class BlocksSource<TOut> : IPipelineElement<TOut> where TOut : Block
    {
        private readonly IBlockProcessor _blockProcessor;
        private readonly ILogger _logger;

        public BlocksSource(IBlockProcessor blockProcessor, ILogger logger)
        {
           _blockProcessor = blockProcessor; 
           _blockProcessor.BlockProcessed += OnBlockProcessed;
           _logger = logger;
        }

        public Action<TOut>? Emit { private get; set; }

        private void OnBlockProcessed(object? sender, BlockProcessedEventArgs args)
        {
            Emit?.Invoke((TOut)args.Block);
        }
    }
}
