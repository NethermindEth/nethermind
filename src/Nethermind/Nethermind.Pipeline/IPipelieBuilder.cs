namespace Nethermind.Pipeline
{
    public interface IPipelineBuilder<TSource ,TOutput>
    {
        IPipelineBuilder<TSource ,TOut> AddElement<TOut>(IPipelineElement<TOutput, TOut> element);
        IPipeline Build();
    }
}