namespace Nethermind.Pipeline
{
    /// <summary>
    /// Interface used for building <see cref="IPipeline"/>. <typeparamref name="TSource"/> is a type of data given to the first element in pipeline, 
    /// and <typeparamref name="TOutput"/> is a type of data emited from the last element in <see cref="IPipeline.Elements"/>. 
    /// </summary>  
    public interface IPipelineBuilder<TSource ,TOutput>
    {
        /// <summary>
        /// Adds <see cref="IPipelineBuilder{TSource, TOutput}"/> to the <see cref="IPipeline.Elements"/>.
        /// </summary>
        IPipelineBuilder<TSource ,TOut> AddElement<TOut>(IPipelineElement<TOutput, TOut> element);

        /// <summary> 
        /// Takes elements added by <see cref="IPipelineBuilder{TSource, TOutput}.AddElement{TOut}(IPipelineElement{TOutput, TOut})"/> and returns <see cref="IPipeline"/>
        /// with set collection of <see cref="IPipeline.Elements"/>. 
        /// </summary>
        IPipeline Build();
    }
}