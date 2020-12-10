using Flazzy;
using Microsoft.Extensions.Configuration.Ini;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using Sulakore.Habbo;
using Sulakore.Habbo.Web;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        static MySqlConnection MySqlConnection;

        static void Main(string[] args)
        {
            hashConfig = GetHashesAsync().GetAwaiter().GetResult();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/69.0.3497.100 Safari/537.36");

            #region MySql
            string connString = "";
            connString += "Server=192.168.178.67;";
            connString += "Port=3306;";
            connString += "Uid=root;";
            connString += "password=123456;";
            connString += "Database=habbo-hashes;";
            MySqlConnection = new MySqlConnection(connString);
            MySqlConnection.Open();
            #endregion

            GenerateResults(args).GetAwaiter().GetResult();
        }

        static string list<T>(IEnumerable<T> enumerable) //https://stackoverflow.com/a/5695117
        {
            List<T> list = new List<T>(enumerable);
            return string.Join(", ", list.ToArray());
        }

        static Dictionary<string,string> LoadHashesWithName(string section)
        {
            var namedHashes = new Dictionary<string, string>();

            bool isInSection = false;
            foreach (var line in hashConfig)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
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

        static async Task GenerateResults(string[] args)
        {
            var hotels = new string[] { ".com", ".fr", ".com.tr", ".nl", ".de", ".it", ".fi", ".es", ".com.br" };
            var files = new List<string>();
            files = args.ToList();

            var incomingHashesWithNames = LoadHashesWithName("Incoming");
            var outgoingHashesWithNames = LoadHashesWithName("Outgoing");

            foreach (var file in files)
            {
                var swfBytes = await File.ReadAllBytesAsync(file);
                Console.WriteLine($"[Updater] Fetched {file}, Size: {swfBytes.Length / 1024}mb");
                var game = new HGame(swfBytes);
                Console.WriteLine($"[Updater] Disassembling SWF");
                game.Disassemble();
                game.GenerateMessageHashes();
                Console.WriteLine($"[Updater] Incoming messages: {game.In.Count}");
                Console.WriteLine($"[Updater] Outgoing messages: {game.Out.Count}");

                var revisionInfo = new RevisionInfo() { Tag = game.Revision, FirstSeen = DateTime.UtcNow };

                foreach (var message in game.In)
                {
                    string name = null;
                    if (incomingHashesWithNames.ContainsKey(message.Hash))
                        name = incomingHashesWithNames[message.Hash];

                    revisionInfo.IncomingMessages.Add(message.Id, new MessageInfo() { Hash = message.Hash, Name = name, Structure = message.Structure, ClassName = message.ClassName, ClassNamespace = message.Class.QName.Namespace.Name, ParserName = message.ParserName, ParserNamespace = message.Parser.QName.Namespace.Name });
                }

                foreach (var message in game.Out)
                {
                    string name = null;
                    if (outgoingHashesWithNames.ContainsKey(message.Hash))
                        name = outgoingHashesWithNames[message.Hash];

                    revisionInfo.OutgoingMessages.Add(message.Id, new MessageInfo() { Hash = message.Hash, Name = name, Structure = message.Structure, ClassName = message.ClassName, ClassNamespace = message.Class.QName.Namespace.Name });
                }

                revisionInfo.IncomingMessages.ToList().ForEach(x => insertSQL(game.Revision, "In", x.Key, x.Value));
                revisionInfo.OutgoingMessages.ToList().ForEach(x => insertSQL(game.Revision, "Out", x.Key, x.Value));

                string json = JsonConvert.SerializeObject(revisionInfo);
                File.WriteAllText($"{basedir}/revisions/{game.Revision}.json", json);
            }
        }

        static void insertSQL(string revision, string direction, int id, MessageInfo messageInfo)
        {
            var messageInsert = new List<MySqlParameter>();

            messageInsert.Add(new MySqlParameter("revision", revision));
            messageInsert.Add(new MySqlParameter("id", id));
            messageInsert.Add(new MySqlParameter("direction", direction));
            messageInsert.Add(new MySqlParameter("hash", messageInfo.Hash));
            messageInsert.Add(new MySqlParameter("Structure", messageInfo.Structure));
            messageInsert.Add(new MySqlParameter("ClassName", messageInfo.ClassName));
            messageInsert.Add(new MySqlParameter("ClassNamespace", messageInfo.ClassNamespace));

            if (direction == "In")
            {
                messageInsert.Add(new MySqlParameter("ParserName", messageInfo.ParserName));
                messageInsert.Add(new MySqlParameter("ParserNamespace", messageInfo.ParserNamespace));
            }

            #region Insert SQL
            var InsertCommand = new MySqlCommand(
                $"INSERT INTO `messages` ({list(messageInsert.Select(x => x.ParameterName).ToList())}) VALUES ({list(messageInsert.Select(x => "@" + x.ParameterName).ToList())})",
                MySqlConnection);
            messageInsert.ForEach(x => InsertCommand.Parameters.AddWithValue("@" + x.ParameterName, x.Value)); //Insert values from list
            InsertCommand.ExecuteNonQuery();
            #endregion
        }

        static async Task<string[]> GetHashesAsync()
        {
            HttpResponseMessage response = await httpClient.GetAsync("https://raw.githubusercontent.com/ArachisH/Sulakore/master/Sulakore/Hashes.ini");
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
