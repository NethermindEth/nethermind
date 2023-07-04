// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DnsClient;
using DnsClient.Protocol;

namespace Nethermind.Network.Dns;

public interface IDnsClient
{
    Task<IEnumerable<string>> Lookup(string query);
}

public class DnsClient : IDnsClient
{
    private readonly string _domain;
    private readonly LookupClient _client;

    public DnsClient(string domain)
    {
        _domain = domain ?? throw new ArgumentNullException(nameof(domain));
        _client = new();
    }

    public async Task<IEnumerable<string>> Lookup(string query)
    {
        if (_client.NameServers.Count == 0)
        {
            return Enumerable.Empty<string>();
        }

        string queryString = $"{(string.IsNullOrWhiteSpace(query) ? string.Empty : (query + "."))}{_domain}";
        DnsQuestion rootQuestion = new(queryString, QueryType.TXT);
        IDnsQueryResponse response = await _client.QueryAsync(rootQuestion, CancellationToken.None);
        return response.Answers.OfType<TxtRecord>().Select(txt => string.Join(string.Empty, txt.Text));
    }
}
