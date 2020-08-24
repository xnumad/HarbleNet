using Flazzy;
using Microsoft.Extensions.Configuration.Ini;
using Newtonsoft.Json;
using Sulakore.Habbo;
using Sulakore.Habbo.Web;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HarbleNet.Updater
{
    class Program
    {
        static HttpClient httpClient = new HttpClient();
        static string[] hashConfig;
        static string basedir = "/var/www/sites/api.harble.net";

        static void Main(string[] args)
        {
            hashConfig = GetHashesAsync().GetAwaiter().GetResult();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/69.0.3497.100 Safari/537.36");
            GenerateResults().GetAwaiter().GetResult();
        }

        static Dictionary<string,string> LoadHashesWithName(string section)
        {
            var namedHashes = new Dictionary<string, string>();

            bool isInSection = false;
            foreach (var line in hashConfig)
            {
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    isInSection = (line == ("[" + section + "]"));
                }
                else if (isInSection)
                {
                    string[] values = line.Split('=');
                    string name = values[0].Trim();
                    string hash = values[1].Trim();

                    if (!namedHashes.ContainsKey(hash))
                        namedHashes.Add(hash, name);
                }
            }

            return namedHashes;
        }

        static async Task GenerateResults()
        {
            var hotels = new string[] { ".com", ".fr", ".com.tr", ".nl", ".de", ".it", ".fi", ".es", ".com.br" };
            var revisions = new List<string>();

            Console.WriteLine($"[Updater] Checking revisions for hotels: [{string.Join(", ", hotels)}]");

            var history = new List<HistoryInfo>();

            foreach (var hotel in hotels)
            {
                var variables = await GetVariablesAsync(hotel);
                var client_url = GetClientUrl(variables);
                var revision = new Regex("PRODUCTION\\-\\d+\\-\\d+").Match(client_url).Value;
                Console.WriteLine($"[Updater] Habbo{hotel} : {revision}");

                if (!revisions.Contains(revision))
                    revisions.Add(revision);

                history.Add(new HistoryInfo() { Hotel = hotel, Revision = revision, LastChecked = DateTime.UtcNow });
            }

            File.WriteAllText($"{basedir}/last.json", JsonConvert.SerializeObject(history));

            var incomingHashesWithNames = LoadHashesWithName("Incoming");
            var outgoingHashesWithNames = LoadHashesWithName("Outgoing");

            foreach (var revision in revisions)
            {
                if (File.Exists($"{basedir}/revisions/{revision}.json"))
                {
                    Console.WriteLine($"[Updater] Already fetched {revision}");
                    continue;
                }

                var swfBytes = await GetClientSwfAsync(revision);
                Console.WriteLine($"[Updater] Fetched {revision}, Size: {swfBytes.Length / 1024}mb");
                var game = new HGame(swfBytes);
                Console.WriteLine($"[Updater] Disassembling SWF");
                game.Disassemble();
                game.GenerateMessageHashes();
                Console.WriteLine($"[Updater] Incoming messages: {game.In.Count}");
                Console.WriteLine($"[Updater] Outgoing messages: {game.Out.Count}");

                var revisionInfo = new RevisionInfo() { Tag = revision, FirstSeen = DateTime.UtcNow };

                foreach (var message in game.In)
                {
                    string name = null;
                    if (incomingHashesWithNames.ContainsKey(message.Hash))
                        name = incomingHashesWithNames[message.Hash];

                    revisionInfo.IncomingMessages.Add(message.Id, new MessageInfo() { Hash = message.Hash, Name = name });
                }

                foreach (var message in game.Out)
                {
                    string name = null;
                    if (outgoingHashesWithNames.ContainsKey(message.Hash))
                        name = outgoingHashesWithNames[message.Hash];

                    revisionInfo.OutgoingMessages.Add(message.Id, new MessageInfo() { Hash = message.Hash, Name = name });
                }

                string json = JsonConvert.SerializeObject(revisionInfo);
                File.WriteAllText($"{basedir}/revisions/{revision}.json", json);
            }
        }

        static async Task<string[]> GetHashesAsync()
        {
            HttpResponseMessage response = await httpClient.GetAsync("https://raw.githubusercontent.com/ArachisH/Tanji/master/Tanji/Hashes.ini");
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync();
                return result.Split('\n');
            }
            else
            {
                return null;
            }
        }

        static async Task<string[]> GetVariablesAsync(string hotel = ".com")
        {
            HttpResponseMessage response = await httpClient.GetAsync($"https://www.habbo{hotel}/gamedata/external_variables/x");
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync();
                return result.Split('\n');
            }
            else
            {
                return null;
            }
        }

        static string GetClientUrl(string[] variables)
        {
            return Array.Find(variables, s => s.StartsWith("flash.client.url=")).Split('=')[1];
        }

        static async Task<byte[]> GetClientSwfAsync(string revision)
        {
            HttpResponseMessage response = await httpClient.GetAsync($"https://images.habbo.com/gordon/{revision}/Habbo.swf");
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsByteArrayAsync();
            else
                return null;
        }
    }
}
