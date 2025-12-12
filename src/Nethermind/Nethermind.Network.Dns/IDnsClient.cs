// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DnsClient;
using DnsClient.Protocol;

namespace Nethermind.Network.Dns;

public interface IDnsClient
{
    Task<IEnumerable<string>> Lookup(string query, CancellationToken cancellationToken = default);
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

    public async Task<IEnumerable<string>> Lookup(string query, CancellationToken cancellationToken = default)
    {
        if (_client.NameServers.Count == 0)
        {
            return [];
        }

        string queryString = $"{(string.IsNullOrWhiteSpace(query) ? string.Empty : (query + "."))}{_domain}";
        DnsQuestion rootQuestion = new(queryString, QueryType.TXT);
        IDnsQueryResponse response = await _client.QueryAsync(rootQuestion, cancellationToken);
        return response.Answers.OfType<TxtRecord>().Select(static txt => string.Join(string.Empty, txt.Text));
    }
}
