namespace Nethermind.Pipeline
{
    public interface IPipelinePublisher<T>
    {
        void Publish();
    }
}