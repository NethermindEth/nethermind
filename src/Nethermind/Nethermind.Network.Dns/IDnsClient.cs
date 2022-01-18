//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using DnsClient;
using DnsClient.Protocol;

namespace Nethermind.Network.Dns;

public interface IDnsClient
{
    string[][] Lookup(string query);
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
    
    public string[][] Lookup(string query)
    {
        string queryString = (string.IsNullOrWhiteSpace(query) ? "" : (query + ".")) + _domain;
        DnsQuestion rootQuestion = new(queryString, QueryType.TXT);
        IDnsQueryResponse response = _client.Query(rootQuestion);
        return response.Answers.OfType<TxtRecord>().Select(txt => txt.Text.ToArray()).ToArray();
    }
}
