using System.Collections.Generic;

namespace Nethermind.DataMarketplace.Providers.Plugins.WebApi
{
    public interface INdmWebApiPlugin : INdmPlugin
    {
        string? Url { get; }
        string? Method { get; }
        IDictionary<string, string>? Headers { get; }
        IDictionary<string, string>? Errors { get; }
        IDictionary<string, string>? QueryString { get; }
    }
}