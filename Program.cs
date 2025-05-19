using Microsoft.Extensions.Configuration;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;

class Program
{
    static async Task Main(string[] args)
    {
        // Загрузка конфигурации
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"))
            .Build();

        // Создание бота
        var botClient = new TelegramBotClient(configuration["BotToken"]);
        var bot = new TelegramBotService(botClient, configuration);
        await botClient.DeleteWebhookAsync();

        // Настройка обработчика обновлений
        using var cts = new CancellationTokenSource();

        // Обработка сигналов завершения (SIGTERM, Ctrl+C)
        AppDomain.CurrentDomain.ProcessExit += (s, e) => cts.Cancel();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true; // Предотвращаем немедленное завершение
            cts.Cancel();
        };

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [] // Все типы обновлений
        };

        // Запуск получения обновлений
        botClient.StartReceiving(
            updateHandler: bot.HandleUpdateAsync,
            pollingErrorHandler: bot.HandlePollingErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: cts.Token
        );

        var me = await botClient.GetMeAsync();
        Console.WriteLine($"Start listening for @{me.Username}");

        // Настройка команд бота
        await botClient.SetMyCommandsAsync([
            new BotCommand { Command = "start", Description = "Создать новый заказ" },
            new BotCommand { Command = "getid", Description = "Получить ID чата" }
        ]);

        try
        {
            // Ожидание завершения (вместо Console.ReadLine)
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("Bot is shutting down...");
        }

        // Отмена получения обновлений
        cts.Cancel();
    }
}