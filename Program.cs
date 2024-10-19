using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Text.Json;

namespace Analitic
{
    class Program
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private static readonly TelegramBotClient client = new TelegramBotClient("Your_Token");
        private static readonly List<string> availableTeams = new List<string>(50);//50*

        private static Dictionary<long, List<int>> subscriptions = new Dictionary<long, List<int>>();//словарь подписок

        private static async Task Main(string[] args)
        {
            await LoadAvailableTeams();
            Timer timer = new Timer(CheckMatches, null, 0, 60000);//milisec
            client.StartReceiving(HandleUpdateAsync, HandleErrorAsync);
            Console.WriteLine("Бот запущен...");
            Console.ReadLine();
        }

        private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            var message = update.Message;
            if (message?.Text != null)
            {
                if (string.Equals("/start", message.Text))
                {
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Привет! Напиши /teams, чтобы получить список команд.");
                }
                else if (string.Equals("/teams", message.Text))
                {
                    await SendAvailableTeams(botClient, message.Chat.Id);
                }
                else if (message.Text.StartsWith("/id_match "))//
                {
                    var splitMessage = message.Text.Split(' ');

                    if (splitMessage.Length < 2)
                    {
                        await botClient.SendTextMessageAsync(message.Chat, "Пожалуйста, укажите ID матча после команды, например: /id_match 1234567890");
                        return;
                    }

                    var matchId = splitMessage[1];
                    var matchInfo = await GetMatchDetails(matchId);
                    await botClient.SendTextMessageAsync(message.Chat, matchInfo);
                }
                else if (message.Text.StartsWith("/sub "))//
                {
                    var splitMessage = message.Text.Split(' ');
                    if (splitMessage.Length < 2 || !int.TryParse(splitMessage[1], out int teamId))
                    {
                        await botClient.SendTextMessageAsync(message.Chat.Id, "Пожалуйста, укажите валидный ID команды.");
                        return;
                    }

                    if (!subscriptions.ContainsKey(message.Chat.Id))
                    {
                        subscriptions[message.Chat.Id] = new List<int>();
                    }
                    if (!subscriptions[message.Chat.Id].Contains(teamId))
                    {
                        subscriptions[message.Chat.Id].Add(teamId);
                        await botClient.SendTextMessageAsync(message.Chat.Id, $"Вы подписались на рассылку оповещений о сыгранный матчах командой ID: {teamId}");
                    }
                    else
                    {
                        await botClient.SendTextMessageAsync(message.Chat.Id, $"Вы уже подписаны на рассылку оповещений о сыгранный матчах командой ID: {teamId}");
                    }
                }
                else if (message.Text.StartsWith("/unsub "))
                {
                    var splitMessage = message.Text.Split(' ');

                    if (splitMessage.Length < 2 || !int.TryParse(splitMessage[1], out int teamId))
                    {
                        await botClient.SendTextMessageAsync(message.Chat.Id, "Пожалуйста, укажите валидный ID команды.");
                        return;
                    }

                    if (subscriptions.ContainsKey(message.Chat.Id) && subscriptions[message.Chat.Id].Contains(teamId))
                    {
                        subscriptions[message.Chat.Id].Remove(teamId);
                        await botClient.SendTextMessageAsync(message.Chat.Id, $"Вы отписались от рассылки оповещений о сыгранных матчах командой ID: {teamId}");
                    }
                    else
                    {
                        await botClient.SendTextMessageAsync(message.Chat.Id, $"Вы не подписаны на рассылку оповещений о сыгранных матчах командой ID: {teamId}");
                    }
                }
                else
                {
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Неизвестная команда");
                }
            }
        }

        private static async Task LoadAvailableTeams()
        {
            var response = await httpClient.GetStringAsync("https://api.opendota.com/api/teams");
            var teams = JArray.Parse(response);

            foreach (var team in teams.Take(50))
            {
                availableTeams.Add($"{team["team_id"]} - {team["name"]}");
            }
        }
        private static async Task SendAvailableTeams(ITelegramBotClient botClient, long chatId)
        {
            var teamsList = string.Join("\n", availableTeams);
            await botClient.SendTextMessageAsync(chatId, $"Доступные команды:\n{teamsList}\n\nИспользуйте /sub <ID команды>, чтобы подписаться на уведомления о матчах.");
        }

        public static async Task<string> GetMatchDetails(string matchId)
        {
            try
            {
                var url = $"https://api.opendota.com/api/matches/{matchId}";
                using var response = await httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();


                using var stream = await response.Content.ReadAsStreamAsync();

                var matchData = await JObject.LoadAsync(new JsonTextReader(new StreamReader(stream)));
                var durationInS = (int)matchData["duration"];
                var durationS = durationInS % 60;
                var durationM = durationInS / 60;

                return $"ID матча: {matchData["match_id"]}\n" +
                       $"Длительность игры: {durationM + ":" + durationS}\n" +
                       $"Победа Сил Света: {matchData["radiant_win"]}\n" +
                       $"Счёт Сил Света: {matchData["radiant_score"]}\n" +
                       $"Счёт Сил Тьмы: {matchData["dire_score"]}"
                       ;
            }
            catch (HttpRequestException ex)
            {
                return "Ошибка при запросе к OpenDota API: " + ex.Message;
            }
            catch (Exception ex)
            {
                return "Произошла ошибка: " + ex.Message;
            }
        }

        private static async void CheckMatches(object state)
        {
            var tasks = new List<Task>();// Таски для каджого пользователя = гарантия параллельного выполнения
            foreach (var subscription in subscriptions)
            {
                long chatId = subscription.Key;

                foreach (int teamId in subscription.Value)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        var matchResult = await GetLastMatchForTeam(teamId);
                        if (!string.IsNullOrEmpty(matchResult))
                        {
                            await client.SendTextMessageAsync(chatId, matchResult);
                        }
                    }));
                }
            }
            Task.WhenAll(tasks).Wait();
        }


        public static async Task<string> GetLastMatchForTeam(int teamId)
        {
            try
            {
                var url = $"https://api.opendota.com/api/teams/{teamId}/matches";
                using var response = await httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();

                using var jsonDocument = await JsonDocument.ParseAsync(stream);
                var matches = jsonDocument.RootElement.EnumerateArray().ToList();

                if (matches.Count > 0)
                {
                    var latestMatch = matches[0];

                    int radiantScore = latestMatch.GetProperty("radiant_score").GetInt32();
                    int direScore = latestMatch.GetProperty("dire_score").GetInt32();
                    long startTime = latestMatch.GetProperty("start_time").GetInt64();
                    long matchId = latestMatch.GetProperty("match_id").GetInt64();

                    return $"Последний матч сыгранный командой {teamId}.\n" +
                           $"Результат: Radiant {radiantScore} - Dire {direScore}.\n" +
                           $"Дата: {DateTimeOffset.FromUnixTimeSeconds(startTime).DateTime}.\n" +
                           $"Match ID: {matchId}";
                }

                return $"У команды {teamId} нет сыгранных матчей.";
            }
            catch (HttpRequestException ex)
            {
                return "Ошибка при запросе к OpenDota API: " + ex.Message;
            }
            catch (Exception ex)
            {
                return "Произошла ошибка: " + ex.Message;
            }
        }

        private static Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine($"Ошибка: {exception.Message}");
            return Task.CompletedTask;
        }
    }
}
