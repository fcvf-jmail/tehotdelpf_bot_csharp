using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
public class TelegramBotService
{
    private readonly ITelegramBotClient _botClient;
    private readonly IConfiguration _configuration;
    private readonly string _ordersFilePath;
    private readonly string _adminChatId;
    private readonly Dictionary<long, UserState> _userStates = [];
    private readonly List<string> _supportedFormats = ["txt", "xlsx", "xls"];

    public TelegramBotService(ITelegramBotClient botClient, IConfiguration configuration)
    {
        _botClient = botClient;
        _configuration = configuration;
        _ordersFilePath = Path.Combine(Directory.GetCurrentDirectory(), "orders.json");
        _adminChatId = configuration["AdminChatId"];
    }

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            if (update.Type == UpdateType.Message && update.Message != null)
            {
                var message = update.Message;
                var chatId = message.Chat.Id;

                if (message.Text != null && message.Text.StartsWith("/start"))
                {
                    await StartDomainScene(chatId, message.MessageId, cancellationToken);
                }
                else if (message.Text != null && message.Text.StartsWith("/getid"))
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: chatId.ToString(),
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await ProcessMessageInScene(chatId, message, cancellationToken);
                }
            }
            else if (update.Type == UpdateType.CallbackQuery)
            {
                var callbackQuery = update.CallbackQuery;
                var chatId = callbackQuery.Message.Chat.Id;

                if (callbackQuery.Data.StartsWith("test_done") ||
                    callbackQuery.Data.StartsWith("work_done") ||
                    callbackQuery.Data.StartsWith("edit_done") ||
                    callbackQuery.Data.StartsWith("stop_done"))
                {
                    await ProcessDoneCallback(callbackQuery, cancellationToken);
                }
                else
                {
                    await ProcessCallbackInScene(chatId, callbackQuery, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling update: {ex.Message}");
        }
    }

    private async Task ProcessDoneCallback(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var match = Regex.Match(callbackQuery.Data, @"^.*_done(\d+)$");
        if (match.Success)
        {
            int orderId = int.Parse(match.Groups[1].Value);
            var order = GetOrder(orderId);

            if (order != null)
            {
                // if (order.AdminsMessage.ButtonTouched) 
                // {
                //     await _botClient.SendTextMessageAsync(
                //         chatId: callbackQuery.Message.Chat.Id,
                //         text: "Этот заказ уже был отработан ранее",
                //         cancellationToken: cancellationToken);
                //     return;
                // }

                SetButtonTouched(orderId);

                await _botClient.SendTextMessageAsync(
                    chatId: order.ChatId,
                    text: $"<b>✅ {order.Answer}</b>\n\n{string.Join("\n", order.Domains)}",
                    parseMode: ParseMode.Html,
                    cancellationToken: cancellationToken);

                await _botClient.SendTextMessageAsync(
                    chatId: order.ChatId,
                    text: "<b>Для нового заказа нажмите /start</b>",
                    parseMode: ParseMode.Html,
                    cancellationToken: cancellationToken);

                string prefix = "✅";
                if (order.AdminsMessage.ButtonText.Contains("отключен"))
                    prefix = "🛑";

                await _botClient.EditMessageReplyMarkupAsync(
                    chatId: order.AdminsMessage.ChatId,
                    messageId: order.AdminsMessage.MessageId,
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData(
                                $"{prefix} {order.AdminsMessage.ButtonText}",
                                "processedOrder"
                            )
                        }
                    }),
                    cancellationToken: cancellationToken);

                // Сообщаем пользователю, что обработка запроса успешно выполнена
                await _botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: "Заказ успешно обработан",
                    cancellationToken: cancellationToken);
            }
        }
    }

    public Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException => $"Telegram API Error: [{apiRequestException.ErrorCode}] {apiRequestException.Message}",
            _ => exception.ToString()
        };

        Console.WriteLine(errorMessage);
        return Task.CompletedTask;
    }

    // Domain Scene Methods
    private async Task StartDomainScene(long chatId, int messageId, CancellationToken cancellationToken)
    {
        _userStates[chatId] = new UserState
        {
            Stage = Stage.EnteringDomains,
            StartMessageId = messageId
        };

        // Если есть информация о пользователе, сохраняем его username
        var user = await _botClient.GetChatMemberAsync(chatId, chatId, cancellationToken);
        if (user != null && !string.IsNullOrEmpty(user.User.Username))
        {
            _userStates[chatId].Username = user.User.Username;
        }

        await _botClient.SendTextMessageAsync(
            chatId: chatId,
            text: "Введите домен\nЕсли их несколько, то каждый с новой строки. Пример:\nexample1.com\nexample2.com",
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("Отмена", "cancel") }
            }),
            cancellationToken: cancellationToken);
    }

    private async Task ProcessMessageInScene(long chatId, Message message, CancellationToken cancellationToken)
    {
        if (!_userStates.TryGetValue(chatId, out var state))
            return;

        switch (state.Stage)
        {
            case Stage.EnteringDomains:
                if (message.Text != null)
                {
                    await ProcessDomainsInput(chatId, message, cancellationToken);
                }
                else
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "Пожалуйста, введите домены текстом.",
                        cancellationToken: cancellationToken);
                }
                break;
            case Stage.EnteringKeywords:
                await ProcessKeywordsInput(chatId, message, cancellationToken);
                break;
            default:
                break;
        }
    }

    private async Task ProcessCallbackInScene(long chatId, CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        if (!_userStates.TryGetValue(chatId, out var state))
            return;

        string callbackData = callbackQuery.Data;

        // Всегда отвечаем на callback, чтобы избежать "залипших" часиков в интерфейсе
        await _botClient.AnswerCallbackQueryAsync(
            callbackQueryId: callbackQuery.Id,
            cancellationToken: cancellationToken);

        if (callbackData == "cancel")
        {
            await CancelOrder(chatId, cancellationToken);
            return;
        }

        switch (state.Stage)
        {
            case Stage.EnteringKeywords:
                if (callbackData == "skip")
                {
                    state.Keywords = "skipped";
                    state.Stage = Stage.ChoosingAction;
                    await DisplayActionSelection(chatId, state, cancellationToken);
                }
                else if (callbackData == "back_to_domain")
                {
                    state.Stage = Stage.EnteringDomains;
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "Введите домен\nЕсли их несколько, то каждый с новой строки. Пример:\nexample1.com\nexample2.com",
                        replyMarkup: new InlineKeyboardMarkup(new[]
                        {
                            new[] { InlineKeyboardButton.WithCallbackData("Отмена", "cancel") }
                        }),
                        cancellationToken: cancellationToken);
                }
                break;
            case Stage.ChoosingAction:
                if (callbackData == "back_to_keywords")
                {
                    state.Stage = Stage.EnteringKeywords;
                    await RequestKeywords(chatId, cancellationToken);
                }
                else if (new[] { "test", "work", "edit", "stop" }.Contains(callbackData))
                {
                    await ProcessActionSelection(chatId, callbackData, state, cancellationToken);
                }
                break;
        }
    }

    private async Task ProcessDomainsInput(long chatId, Message message, CancellationToken cancellationToken)
    {
        var state = _userStates[chatId];
        state.Domains = message.Text.Split('\n').Select(d => d.Trim()).ToList();
        state.Stage = Stage.EnteringKeywords;

        await RequestKeywords(chatId, cancellationToken);
    }

    private async Task RequestKeywords(long chatId, CancellationToken cancellationToken)
    {
        string messageText = "Введите ключевые слова:";
        foreach (var format in _supportedFormats)
        {
            messageText += $"\n- Файл .{format}";
        }
        messageText += "\n- Фото (можно с текстом)\n- Ссылка на Топвизор (Пример: https://tpv.sr/uhHFV4-9C/)";

        await _botClient.SendTextMessageAsync(
            chatId: chatId,
            text: messageText,
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("Пропустить", "skip") },
                new[] { InlineKeyboardButton.WithCallbackData("Назад", "back_to_domain") },
                new[] { InlineKeyboardButton.WithCallbackData("Отмена", "cancel") }
            }),
            cancellationToken: cancellationToken);
    }

    private async Task ProcessKeywordsInput(long chatId, Message message, CancellationToken cancellationToken)
    {
        if (!_userStates.TryGetValue(chatId, out var state))
            return;

        bool isValid = false;

        if (message.Text != null && message.Text.StartsWith("https://tpv.sr/"))
        {
            state.Keywords = message.Text;
            isValid = true;
        }
        else if (message.Document != null)
        {
            string fileName = message.Document.FileName;
            if (_supportedFormats.Any(format => fileName.EndsWith($".{format}")))
            {
                state.Keywords = $"file_id: {message.Document.FileId}";
                isValid = true;
            }
        }
        else if (message.Photo != null && message.Photo.Length > 0)
        {
            state.PhotoCaption = message.Caption;
            state.Keywords = $"photo_file_id: {message.Photo.Last().FileId}";
            isValid = true;
        }

        if (!isValid)
        {
            string messageText = "Некорректный ввод\nОтправьте ключевые слова:";
            foreach (var format in _supportedFormats)
            {
                messageText += $"\n- Файл .{format}";
            }
            messageText += "\n- Фотография\n- Ссылка на Топвизор (Пример: https://tpv.sr/uhHFV4-9C/)";

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: messageText,
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                    [InlineKeyboardButton.WithCallbackData("Назад", "back_to_domain")],
                    new[] { InlineKeyboardButton.WithCallbackData("Отмена", "cancel") }
                }),
                cancellationToken: cancellationToken);
            return;
        }

        state.Stage = Stage.ChoosingAction;
        await DisplayActionSelection(chatId, state, cancellationToken);
    }

    private async Task DisplayActionSelection(long chatId, UserState state, CancellationToken cancellationToken)
    {
        bool moreThanOneDomain = state.Domains.Count > 1;

        await _botClient.SendTextMessageAsync(
            chatId: chatId,
            text: "Выберите действие:",
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                [InlineKeyboardButton.WithCallbackData($"Запустить тест{(moreThanOneDomain ? "ы" : "")}", "test")],
                [InlineKeyboardButton.WithCallbackData("Запустить в работу", "work")],
                [InlineKeyboardButton.WithCallbackData("Произвести правки", "edit")],
                [InlineKeyboardButton.WithCallbackData("Поставить на стоп", "stop")],
                [InlineKeyboardButton.WithCallbackData("Назад", "back_to_keywords")],
                new[] { InlineKeyboardButton.WithCallbackData("Отмена", "cancel") }
            }),
            cancellationToken: cancellationToken);
    }

    private async Task ProcessActionSelection(long chatId, string action, UserState state, CancellationToken cancellationToken)
    {
        state.Action = action;
        bool moreThanOneDomain = state.Domains.Count > 1;
        int orderId = await GetNewId();

        var statusToAnswer = new Dictionary<string, string>
        {
            ["test"] = $"Тест{(moreThanOneDomain ? "ы" : "")} запущен{(moreThanOneDomain ? "ы" : "")}",
            ["work"] = $"Проект{(moreThanOneDomain ? "ы" : "")} запущен{(moreThanOneDomain ? "ы" : "")}",
            ["edit"] = "Правки произведены",
            ["stop"] = $"Проект{(moreThanOneDomain ? "ы" : "")} отключен{(moreThanOneDomain ? "ы" : "")}"
        };

        var statusToText = new Dictionary<string, string>
        {
            ["test"] = $"Домен{(moreThanOneDomain ? "ы" : "")} на тест",
            ["work"] = $"Домен{(moreThanOneDomain ? "ы" : "")} в работу",
            ["edit"] = $"Домен{(moreThanOneDomain ? "ы" : "")} на правки",
            ["stop"] = $"Домен{(moreThanOneDomain ? "ы" : "")} на стоп"
        };

        bool containsFile = state.Keywords.StartsWith("file_id:");
        bool containsPhoto = state.Keywords.StartsWith("photo_file_id:");
        bool keywordsAreSkipped = state.Keywords == "skipped";

        string messageText = $"<b>{statusToText[action]}</b>\n\nДомены:\n{string.Join("\n", state.Domains)}";

        if (!keywordsAreSkipped)
        {
            if (containsFile)
                messageText += "\n\nКлючевые слова: в файле";
            else if (containsPhoto)
                messageText += $"\n\nКлючевые слова: на фото + {state.PhotoCaption}";
            else
                messageText += $"\n\nКлючевые слова: {state.Keywords}";
        }

        var statusToButton = new Dictionary<string, InlineKeyboardMarkup>
        {
            ["test"] = new InlineKeyboardMarkup(new[] { InlineKeyboardButton.WithCallbackData(statusToAnswer[action], $"test_done{orderId}") }),
            ["work"] = new InlineKeyboardMarkup(new[] { InlineKeyboardButton.WithCallbackData(statusToAnswer[action], $"work_done{orderId}") }),
            ["edit"] = new InlineKeyboardMarkup(new[] { InlineKeyboardButton.WithCallbackData(statusToAnswer[action], $"edit_done{orderId}") }),
            ["stop"] = new InlineKeyboardMarkup(new[] { InlineKeyboardButton.WithCallbackData(statusToAnswer[action], $"stop_done{orderId}") })
        };

        // Уведомление админа о новом заказе
        if (!string.IsNullOrEmpty(state.Username))
        {
            await _botClient.SendTextMessageAsync(
                chatId: _adminChatId,
                text: $"Заказ от @{state.Username}",
                cancellationToken: cancellationToken);
        }
        else
        {
            await _botClient.ForwardMessageAsync(
                chatId: _adminChatId,
                fromChatId: chatId,
                messageId: state.StartMessageId,
                cancellationToken: cancellationToken);
        }

        // Отправка данных заказа
        Message adminMessage;
        if (containsFile)
        {
            // Создаем InputFileId из строки с идентификатором файла
            var fileId = state.Keywords.Replace("file_id: ", "");
            var inputFile = new InputFileId(fileId);

            adminMessage = await _botClient.SendDocumentAsync(
                chatId: _adminChatId,
                document: inputFile,
                caption: messageText,
                parseMode: ParseMode.Html,
                replyMarkup: statusToButton[action],
                cancellationToken: cancellationToken);
        }
        else if (containsPhoto)
        {
            // Создаем InputFileId из строки с идентификатором фото
            var photoId = state.Keywords.Replace("photo_file_id: ", "");
            var inputFile = new InputFileId(photoId);

            adminMessage = await _botClient.SendPhotoAsync(
                chatId: _adminChatId,
                photo: inputFile,
                caption: messageText,
                parseMode: ParseMode.Html,
                replyMarkup: statusToButton[action],
                cancellationToken: cancellationToken);
        }
        else
        {
            adminMessage = await _botClient.SendTextMessageAsync(
                chatId: _adminChatId,
                text: messageText,
                parseMode: ParseMode.Html,
                replyMarkup: statusToButton[action],
                cancellationToken: cancellationToken);
        }

        var adminsMessageInfo = new AdminsMessage
        {
            ChatId = adminMessage.Chat.Id,
            MessageId = adminMessage.MessageId,
            ButtonText = statusToAnswer[action],
            ButtonTouched = false
        };

        await WriteNewOrder(orderId, chatId, state.Domains, statusToAnswer[action], adminsMessageInfo);

        await _botClient.SendTextMessageAsync(
            chatId: chatId,
            text: "Ваш заказ передан в тех отдел. Ожидайте",
            cancellationToken: cancellationToken);

        await _botClient.SendTextMessageAsync(
            chatId: chatId,
            text: "<b>Для нового заказа нажмите /start</b>",
            parseMode: ParseMode.Html,
            cancellationToken: cancellationToken);

        // Очистка состояния пользователя
        _userStates.Remove(chatId);
    }

    private async Task CancelOrder(long chatId, CancellationToken cancellationToken)
    {
        await _botClient.SendTextMessageAsync(
            chatId: chatId,
            text: "Заказ отменен, для новой заявки нажмите /start",
            cancellationToken: cancellationToken);

        _userStates.Remove(chatId);
    }

    // Orders management methods
    private Order GetOrder(int orderId)
    {
        if (!System.IO.File.Exists(_ordersFilePath))
            System.IO.File.WriteAllText(_ordersFilePath, "[]");

        var orders = JsonConvert.DeserializeObject<List<Order>>(System.IO.File.ReadAllText(_ordersFilePath)) ?? new List<Order>();
        return orders.FirstOrDefault(o => o.OrderId == orderId);
    }

    private async Task<int> GetNewId()
    {
        if (!System.IO.File.Exists(_ordersFilePath))
            System.IO.File.WriteAllText(_ordersFilePath, "[]");

        var orders = JsonConvert.DeserializeObject<List<Order>>(System.IO.File.ReadAllText(_ordersFilePath)) ?? new List<Order>();
        int lastOrderId = orders.Count > 0 ? orders.Max(o => o.OrderId) : 0;
        return lastOrderId + 1;
    }

    private async Task WriteNewOrder(int orderId, long chatId, List<string> domains, string answer, AdminsMessage adminsMessage)
    {
        if (!System.IO.File.Exists(_ordersFilePath))
            System.IO.File.WriteAllText(_ordersFilePath, "[]");

        var orders = JsonConvert.DeserializeObject<List<Order>>(System.IO.File.ReadAllText(_ordersFilePath)) ?? new List<Order>();
        orders.Add(new Order
        {
            OrderId = orderId,
            ChatId = chatId,
            Domains = domains,
            Answer = answer,
            AdminsMessage = adminsMessage
        });

        System.IO.File.WriteAllText(_ordersFilePath, JsonConvert.SerializeObject(orders, Formatting.Indented));
    }

    private void SetButtonTouched(int orderId)
    {
        if (!System.IO.File.Exists(_ordersFilePath))
            System.IO.File.WriteAllText(_ordersFilePath, "[]");

        var orders = JsonConvert.DeserializeObject<List<Order>>(System.IO.File.ReadAllText(_ordersFilePath)) ?? new List<Order>();
        var order = orders.FirstOrDefault(o => o.OrderId == orderId);
        if (order != null)
        {
            order.AdminsMessage.ButtonTouched = true;
            System.IO.File.WriteAllText(_ordersFilePath, JsonConvert.SerializeObject(orders, Formatting.Indented));
        }
    }
}