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

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;



namespace Nethermind.Pipeline.Publishers
{
    public class DiscordPublisher : IPublisher
    {
        private readonly IJsonSerializer _serializer;
        private readonly string _chatId;
        private string _botToken;
        private readonly HttpClient _httpClient;
        private bool _isEnabled;

        public DiscordPublisher(IJsonSerializer serializer, string chatId)
        {
            _serializer = serializer;
            _chatId = $"{chatId}";
            _botToken = LoadBotToken();
            _httpClient = new HttpClient();

            Start();
       
        }

        public void SubscribeToData<T>(T data)
        {
            if (!_isEnabled) return;
            var task = Task.Run(() => SendMessageAsync(data));
            task.Wait();
        }

        public void Stop()
        {
            _isEnabled = false;
        }

        public void Start()
        {
            _isEnabled = true;
        }

        private async Task SendMessageAsync(object data)
        {

            var serializedData = _serializer.Serialize(data);

            var headers = _httpClient.DefaultRequestHeaders;

            var uri = new Uri($"https://discordapp.com/api/channels/{_chatId}/messages");

            var messageContents = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("chat_id", _chatId),
                new KeyValuePair<string, string>("text", serializedData)
            });

            var message = await messageContents.ReadAsStringAsync();

            string content = "{\"content\":\"" + message + "\"}";

            _httpClient.DefaultRequestHeaders.Add("Authorization", "Bot " + _botToken);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Nethermind Bot (https://nethermind.io, v1.0)");

            try {

              var x = await _httpClient.PostAsync(uri, new StringContent(content, Encoding.UTF8, "application/json"));

                // Console.WriteLine(x);

            } catch (HttpRequestException ex) {
                Console.WriteLine(ex);
            }//catch
        }//SendMessageAsync

        private string LoadBotToken()
        {
            //TODO: Generate Bot Token
            return "ODYxOTkyOTIyNjIxNjczNTEy.YOR3dw.lQ3-VEaJShvuUT986b9jwK47y8s"; //this is just a test bot, will do a proper secret token after tests are done and we will create another bot
        }

    }

    class DiscordMessage
    {
        public string content { get; set;}
    }
}
