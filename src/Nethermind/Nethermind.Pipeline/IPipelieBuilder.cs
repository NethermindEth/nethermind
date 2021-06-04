using System.Diagnostics;
using Nethermind.Pipeline.Publishers;

namespace Nethermind.Pipeline
{
    /// <summary>
    /// Interface used for building <see cref="IPipeline"/>. <typeparamref name="TSource"/> is a type of data given to the first element in pipeline, 
    /// and <typeparamref name="TOutput"/> is a type of data emited from the last element in <see cref="IPipeline.Elements"/>. 
    /// </summary>  
    public interface IPipelineBuilder<TSource ,TOutput> : IPipelineBuilder
    {
        /// <summary>
        /// Adds <see cref="IPipelineBuilder{TSource, TOutput}"/> to the <see cref="IPipeline.Elements"/>.
        /// </summary>
        IPipelineBuilder<TSource ,TOut> AddElement<TOut>(IPipelineElement<TOutput, TOut> element);

        /// <summary>
        /// Adds <see cref="IWebSocketsPublisher"/> to the <see cref="IPipeline.Elements"/>. 
        /// </summary>
        /// <param name="publisher">Websockets publisher used in pipeline.</param>
        IPipelineBuilder<TSource, TOutput> AddPublisher(IWebSocketsPublisher publisher);
    }

    public interface IPipelineBuilder
    {
        /// <summary>
        /// Lastly added element with <see cref="IPipelineBuilder{TSource, TOutput}.AddElement{TOut}(IPipelineElement{TOutput, TOut})" />
        /// </summary>
        IPipelineElement LastElement { get; }

        /// <summary> 
        /// Takes elements added by <see cref="IPipelineBuilder{TSource, TOutput}.AddElement{TOut}(IPipelineElement{TOutput, TOut})"/> and returns <see cref="IPipeline"/>
        /// with set collection of <see cref="IPipeline.Elements"/>. 
        /// </summary>
        IPipeline Build();
    }
}