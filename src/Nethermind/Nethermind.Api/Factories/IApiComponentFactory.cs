namespace Nethermind.Api.Factories;

public interface IApiComponentFactory<out T>
{
    T Create();
}