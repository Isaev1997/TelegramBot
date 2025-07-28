using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

Dictionary<long, (double lat, double lon)> userLocations = new();
Dictionary<long, double> userSearchRadius = new();

var botToken = Environment.GetEnvironmentVariable("BOT_TOKEN");
if (string.IsNullOrEmpty(botToken))
{
    Console.WriteLine("❌ Переменная окружения BOT_TOKEN не установлена.");
    return;
}
var botClient = new TelegramBotClient(botToken);


var cts = new CancellationTokenSource();

botClient.StartReceiving(
    HandleUpdateAsync,
    HandleErrorAsync,
    new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() },
    cancellationToken: cts.Token
);

var me = await botClient.GetMeAsync();
Console.WriteLine($"Бот запущен: @{me.Username}");
Console.ReadLine();
cts.Cancel();

async Task<List<(string name, double lat, double lon)>> SearchPlacesOverpassAsync(
    double lat, double lon, List<(string key, string value)> tags, double searchRadius)
{
    using var httpClient = new HttpClient();

    double delta = searchRadius / 111.0; // 1° ≈ 111 км
    double south = lat - delta;
    double north = lat + delta;
    double west = lon - delta;
    double east = lon + delta;

    var tagFilters = string.Join("\n", tags.Select(tag =>
        $"node[\"{tag.key}\"=\"{tag.value}\"]({south.ToString(System.Globalization.CultureInfo.InvariantCulture)},{west.ToString(System.Globalization.CultureInfo.InvariantCulture)},{north.ToString(System.Globalization.CultureInfo.InvariantCulture)},{east.ToString(System.Globalization.CultureInfo.InvariantCulture)});"
    ));

    string query = $"""
    [out:json][timeout:25];
    (
      {tagFilters}
    );
    out body;
    """;

    var content = new StringContent($"data={Uri.EscapeDataString(query)}", Encoding.UTF8, "application/x-www-form-urlencoded");
    var response = await httpClient.PostAsync("https://overpass-api.de/api/interpreter", content);

    if (!response.IsSuccessStatusCode)
        return new();

    var json = await response.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(json);
    var result = new List<(string name, double lat, double lon)>();

    if (!doc.RootElement.TryGetProperty("elements", out var elements))
        return result;

    foreach (var item in elements.EnumerateArray())
    {
        double resLat = item.GetProperty("lat").GetDouble();
        double resLon = item.GetProperty("lon").GetDouble();
        string name = item.TryGetProperty("tags", out var tagsNode) &&
                      tagsNode.TryGetProperty("name", out var nameVal)
            ? nameVal.GetString() ?? "Неизвестно"
            : "Неизвестно";

        result.Add((name, resLat, resLon));
    }

    return result;
}

async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken cancellationToken)
{
    var now = DateTime.UtcNow.AddHours(5); // Узбекистан (UTC+5)
    if (now.Hour >= 23 || now.Hour < 7)
    {
        long chatId = update.Message?.Chat.Id
                      ?? update.CallbackQuery?.Message.Chat.Id
                      ?? 0;

        if (chatId != 0)
        {
            await bot.SendTextMessageAsync(
                chatId,
                "⏳ Бот работает ежедневно с 07:00 до 23:00. Попробуйте позже.",
                cancellationToken: cancellationToken
            );
        }

        return;
    }

    if (update.Message?.Location != null)
    {
        var chatId = update.Message.Chat.Id;
        userLocations[chatId] = (update.Message.Location.Latitude, update.Message.Location.Longitude);

        var radiusButtons = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("2 км", "radius_2"),
                InlineKeyboardButton.WithCallbackData("3 км", "radius_3"),
                InlineKeyboardButton.WithCallbackData("6 км", "radius_6")
            }
        });

        await bot.SendTextMessageAsync(
            chatId,
            "Выберите радиус поиска:",
            replyMarkup: radiusButtons,
            cancellationToken: cancellationToken
        );
    }
    else if (update.CallbackQuery != null)
    {
        var chatId = update.CallbackQuery.Message.Chat.Id;
        var data = update.CallbackQuery.Data;

        if (data.StartsWith("radius_"))
        {
            double selectedRadius = double.Parse(data.Replace("radius_", ""));
            userSearchRadius[chatId] = selectedRadius;

            var categoryButtons = new InlineKeyboardMarkup(new[]
{
    new[] { InlineKeyboardButton.WithCallbackData("🛡 Милиция", "police") },
    new[] { InlineKeyboardButton.WithCallbackData("🚦 ГАИ", "traffic") },
    new[] { InlineKeyboardButton.WithCallbackData("🏥 Больница", "hospital") },
    new[] { InlineKeyboardButton.WithCallbackData("🍽 Заведения", "places") },
    new[] { InlineKeyboardButton.WithCallbackData("🏨 Гостиницы", "hotels") },
    new[] { InlineKeyboardButton.WithCallbackData("🏫 Школы и сады", "schools") },
    new[] { InlineKeyboardButton.WithCallbackData("🌦 Погода", "weather") },
    new[] { InlineKeyboardButton.WithCallbackData("🏛 История города", "history") },
    new[] { InlineKeyboardButton.WithCallbackData("🗺 Туризм", "tourism") },
    new[] { InlineKeyboardButton.WithCallbackData("🚨 Экстренные службы", "emergency") },
    new[] { InlineKeyboardButton.WithCallbackData("🔄 Сбросить все запросы", "reset") }
});


            await bot.SendTextMessageAsync(
                chatId,
                $"Радиус установлен: {selectedRadius} км\nТеперь выберите категорию:",
                replyMarkup: categoryButtons,
                cancellationToken: cancellationToken
            );

            return;
        }

        if (!userLocations.TryGetValue(chatId, out var coords))
        {
            await bot.SendTextMessageAsync(chatId, "📍 Сначала отправьте свою геолокацию", cancellationToken: cancellationToken);
            return;
        }

        if (data == "emergency")
        {
            string emergencyText = """
            📞 Экстренные службы Узбекистана:
            🚓 Милиция: 102
            🚒 Пожарная: 101
            🚑 Скорая помощь: 103
            🆘 Единая служба: 112
            ☎️ Справочная: 109
            """;

            await bot.SendTextMessageAsync(chatId, emergencyText, cancellationToken: cancellationToken);
            return;
        }
        if (data == "reset")
        {
            userLocations.Remove(chatId);
            userSearchRadius.Remove(chatId);

            await bot.SendTextMessageAsync(
                chatId,
                "🔄 Данные сброшены.\n📍 Пожалуйста, отправьте свою геолокацию заново.",
                cancellationToken: cancellationToken
            );

            return;
        }

        if (data == "weather")
        {
            string yandex = $"https://yandex.uz/pogoda/?lat={coords.lat}&lon={coords.lon}";
            string windy = $"https://www.windy.com/{coords.lat}/{coords.lon}";

            var buttons = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithUrl("🌦 Yandex", yandex),
                    InlineKeyboardButton.WithUrl("🌪 Windy", windy)
                }
            });

            await bot.SendTextMessageAsync(chatId, "🔎 Выберите источник прогноза погоды:", replyMarkup: buttons, cancellationToken: cancellationToken);
            return;
        }

        if (data == "history")
        {
            var wikiSearchUrl = $"https://ru.wikipedia.org/wiki/Служебная:Search?search={coords.lat}+{coords.lon}";

            await bot.SendTextMessageAsync(
                chatId: chatId,
                text: "🏛 История местности:",
                cancellationToken: cancellationToken
            );

            await bot.SendTextMessageAsync(
                chatId: chatId,
                text: $"🔗 [Открыть поиск в Википедии]({wikiSearchUrl})",
                parseMode: ParseMode.Markdown,
                disableWebPagePreview: false,
                cancellationToken: cancellationToken
            );

            return;
        }

        var tags = new List<(string key, string value)>();
        switch (data)
        {
            case "police":
                tags.Add(("amenity", "police")); break;
            case "traffic":
                tags.Add(("highway", "traffic_signals")); break;
            case "hospital":
                tags.Add(("amenity", "hospital")); break;
            case "places":
                tags.AddRange(new[] { ("amenity", "restaurant"), ("amenity", "cafe"), ("amenity", "fast_food") }); break;
            case "hotels":
                tags.AddRange(new[] { ("tourism", "hotel"), ("tourism", "guest_house"), ("tourism", "motel") }); break;
            case "schools":
                tags.AddRange(new[] { ("amenity", "school"), ("amenity", "kindergarten") }); break;
            case "tourism":
                tags.AddRange(new[] { ("tourism", "attraction"), ("historic", "monument") }); break;
            default:
                await bot.SendTextMessageAsync(chatId, "❌ Неизвестная категория", cancellationToken: cancellationToken);
                return;
        }

        double searchRadius = userSearchRadius.ContainsKey(chatId) ? userSearchRadius[chatId] : 2.0;
        var places = await SearchPlacesOverpassAsync(coords.lat, coords.lon, tags, searchRadius);

        if (places.Count == 0)
        {
            await bot.SendTextMessageAsync(chatId, "❌ Ничего не найдено поблизости", cancellationToken: cancellationToken);
            return;
        }

        var placeButtons = places.Take(5).Select(p => new[]
{
    InlineKeyboardButton.WithUrl($"📍 Yandex: {p.name}", $"https://yandex.uz/maps/?ll={p.lon}%2C{p.lat}&z=16&text={Uri.EscapeDataString(p.name)}"),
    InlineKeyboardButton.WithUrl($"🌍 Google: {p.name}", $"https://www.google.com/maps/search/?api=1&query={p.lat},{p.lon}")
}).ToList();

        // Добавим кнопку сброса как последнюю строку
        placeButtons.Add(new[]
        {
    InlineKeyboardButton.WithCallbackData("🔄 Сбросить все запросы", "reset")
});


        await bot.SendTextMessageAsync(
            chatId,
            "🔍 Вот что нашлось поблизости:",
            replyMarkup: new InlineKeyboardMarkup(placeButtons),
            cancellationToken: cancellationToken
        );
    }
    else if (update.Message?.Text != null)
    {
        await bot.SendTextMessageAsync(update.Message.Chat.Id, "📍 Отправьте свою геолокацию, чтобы я мог помочь!", cancellationToken: cancellationToken);
    }
}

Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken cancellationToken)
{
    Console.WriteLine($"Ошибка: {exception.Message}");
    return Task.CompletedTask;
}
