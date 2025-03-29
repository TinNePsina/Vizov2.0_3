using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Text.Json;
using TaskTracker.Models;

namespace TaskTracker
{
    class Program
    {
        private static ITelegramBotClient _bot = null!;
        private static Storage _storage = new Storage();
        private static string _storagePath = null!;
        private static readonly object _lock = new object();

        static async Task Main()
        {
            try
            {
                var configText = File.ReadAllText("appsettings.json");
                var config = JsonSerializer.Deserialize<Dictionary<string, string>>(configText);

                if (config == null || !config.ContainsKey("StoragePath") || !config.ContainsKey("TelegramBotToken"))
                {
                    Console.WriteLine("Ошибка в конфигурационном файле.");
                    return;
                }

                _storagePath = config["StoragePath"];
                _bot = new TelegramBotClient(config["TelegramBotToken"]);
                
                LoadStorage();
                StartReminderChecker();

                _bot.StartReceiving(UpdateHandler, ErrorHandler);
                
                await _bot.SetMyCommands(
                    commands: new[]
                        {
                        new BotCommand { Command = "/add", Description = "Добавить напоминание" },
                        new BotCommand { Command = "/list", Description = "Список напоминаний" },
                        new BotCommand { Command = "/delete", Description = "Удалить напоминание" },
                        new BotCommand { Command = "/help", Description = "Помощь" }
                        },
                    cancellationToken: CancellationToken.None);

                Console.WriteLine("Бот запущен. Нажмите Enter для выхода.");
                Console.ReadLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при запуске: {ex.Message}");
            }
        }

        private static Task ErrorHandler(ITelegramBotClient bot, Exception error, CancellationToken ct)
        {
            Console.WriteLine($"Ошибка: {error.Message}");
            return Task.CompletedTask;
        }

        private static async Task UpdateHandler(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (update.Message is not { Text: { } text } message)
                return;

            var chatId = message.Chat.Id;
            var parts = text.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0) return;

            switch (parts[0].ToLower())
            {
                case "/start":
                    await SendWelcomeMessage(chatId);
                    break;
                case "/add" when parts.Length == 2:
                    await AddReminder(chatId, parts[1]);
                    break;
                case "/list":
                    await ListReminders(chatId);
                    break;
                case "/delete" when parts.Length == 2:
                    await DeleteReminder(chatId, parts[1]);
                    break;
                case "/help":
                    await SendHelpMessage(chatId);
                    break;
                default:
                    await HandleUnknownCommand(chatId);
                    break;
            }
        }

        private static async Task SendWelcomeMessage(long chatId)
        {
            var message = "👋 Добро пожаловать в бот-напоминалку!\n\n" +
                          "Доступные команды:\n" +
                          "/add HH:mm Текст - Добавить напоминание\n" +
                          "/list - Показать все напоминания\n" +
                          "/delete Номер - Удалить напоминание\n" +
                          "/help - Показать справку";
            
            await _bot.SendTextMessageAsync(
                chatId: chatId,
                text: message,
                parseMode: ParseMode.Markdown,
                cancellationToken: CancellationToken.None);
        }

        private static async Task SendHelpMessage(long chatId)
        {
            var helpText = "ℹ️ *Справка по использованию бота*\n\n" +
                           "*/add HH:mm Текст* - Добавить новое напоминание\n" +
                           "Пример: `/add 14:30 Позвонить маме`\n\n" +
                           "*/list* - Показать все активные напоминания\n\n" +
                           "*/delete Номер* - Удалить напоминание по номеру\n" +
                           "Пример: `/delete 3`";
            
            await _bot.SendTextMessageAsync(
                chatId: chatId,
                text: helpText,
                parseMode: ParseMode.Markdown,
                cancellationToken: CancellationToken.None);
        }

        private static async Task HandleUnknownCommand(long chatId)
        {
            await _bot.SendTextMessageAsync(
                chatId: chatId,
                text: "❌ Неизвестная команда. Используйте /help для списка команд",
                parseMode: ParseMode.Markdown,
                cancellationToken: CancellationToken.None);
        }

        private static async Task AddReminder(long chatId, string input)
        {
            try
            {
                var inputParts = input.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (inputParts.Length != 2) throw new FormatException();

                var time = DateTime.ParseExact(inputParts[0], "HH:mm", null);
                var reminderTime = DateTime.Today.Add(time.TimeOfDay);
                
                if (reminderTime < DateTime.Now)
                {
                    reminderTime = reminderTime.AddDays(1);
                }

                var task = new ReminderTask
                {
                    Id = _storage.Tasks.Count + 1,
                    UserId = chatId,
                    Text = inputParts[1],
                    ReminderTime = reminderTime
                };

                lock (_lock)
                {
                    _storage.Tasks.Add(task);
                    SaveStorage();
                }

                await _bot.SendTextMessageAsync(
                    chatId: chatId,
                    text: $"Задача #{task.Id} добавлена!",
                    cancellationToken: CancellationToken.None);
            }
            catch
            {
                await _bot.SendTextMessageAsync(
                    chatId: chatId,
                    text: "Ошибка формата. Используйте: /add HH:mm Текст",
                    cancellationToken: CancellationToken.None);
            }
        }

        private static async Task ListReminders(long chatId)
        {
            List<ReminderTask> tasks;
            lock (_lock)
            {
                tasks = _storage.Tasks.Where(t => t.UserId == chatId).ToList();
            }

            var response = tasks.Count == 0 
                ? "📭 Нет активных напоминаний" 
                : "📋 Список ваших напоминаний:\n" + string.Join("\n", tasks.Select(t => 
                    $"🕒 *{t.ReminderTime:HH:mm}* \n" +
                    $"🔹 #{t.Id}: {t.Text}"));

            await _bot.SendTextMessageAsync(
                chatId: chatId,
                text: response,
                parseMode: ParseMode.Markdown,
                cancellationToken: CancellationToken.None);
        }

        private static async Task DeleteReminder(long chatId, string input)
        {
            if (int.TryParse(input, out var id))
            {
                lock (_lock)
                {
                    var task = _storage.Tasks.FirstOrDefault(t => t.Id == id && t.UserId == chatId);
                    if (task != null)
                    {
                        _storage.Tasks.Remove(task);
                        SaveStorage();
                        _ = _bot.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"✅ Напоминание #{id} удалено",
                            cancellationToken: CancellationToken.None);
                        return;
                    }
                }
            }
            await _bot.SendTextMessageAsync(
                chatId: chatId,
                text: "❌ Напоминание не найдено",
                cancellationToken: CancellationToken.None);
        }

        private static void LoadStorage()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_storagePath))
                    {
                        _storage = new Storage();
                        return;
                    }
                    
                    var json = File.ReadAllText(_storagePath);
                    _storage = JsonSerializer.Deserialize<Storage>(json) ?? new Storage();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка загрузки данных: {ex.Message}");
                    _storage = new Storage();
                }
            }
        }

        private static void SaveStorage()
        {
            lock (_lock)
            {
                try
                {
                    var json = JsonSerializer.Serialize(_storage);
                    File.WriteAllText(_storagePath, json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка сохранения данных: {ex.Message}");
                }
            }
        }

        private static async void StartReminderChecker()
        {
            while (true)
            {
                List<ReminderTask> tasksToNotify;
                lock (_lock)
                {
                    tasksToNotify = _storage.Tasks
                        .Where(t => t.ReminderTime <= DateTime.Now)
                        .ToList();
                }

                foreach (var task in tasksToNotify)
                {
                    try
                    {
                        await _bot.SendTextMessageAsync(
                            chatId: task.UserId,
                            text: $"⏰ *Напоминание:* {task.Text}",
                            parseMode: ParseMode.Markdown,
                            cancellationToken: CancellationToken.None);

                        lock (_lock)
                        {
                            _storage.Tasks.Remove(task);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка отправки напоминания: {ex.Message}");
                    }
                }

                if (tasksToNotify.Count > 0)
                {
                    SaveStorage();
                }

                await Task.Delay(60000);
            }
        }
    }
}