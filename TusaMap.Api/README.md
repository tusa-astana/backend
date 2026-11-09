# TusaMap API (.NET 10)

Бэкенд TUSA: каталог событий, модерация, RSVP, инструменты организатора и цифровой Organizer Pro через Telegram Stars. Офлайн-билетные модели сохранены как legacy; TUSA не продаёт билеты и не принимает оплату за вход.

## Запуск

```bash
cd backend/TusaMap.Api
dotnet run
```

API: http://localhost:5001. Swagger: http://localhost:5001/swagger

## Локальный режим без сервера

Если `ConnectionStrings:Default` пустой, API использует SQLite-файл `tusamap.db` в папке приложения. Это подходит для разработки на личном компьютере: данные переживают перезапуск, но компьютер должен оставаться включённым.

Запуск:

```powershell
$env:PORT = "5001"
dotnet run --project .\TusaMap.Api\TusaMap.Api.csproj
```

Для frontend в `src/environments/environment.ts` используется `http://localhost:5001`.

> Telegram не сможет обратиться к `localhost` на твоём компьютере пользователя. Для теста внутри Telegram нужен HTTPS-туннель, например Cloudflare Tunnel, который направляет публичный URL на `localhost:5001`.

## Токен бота

Задай токен одним из способов:

- **User Secrets** (рекомендуется для разработки):
  ```bash
  dotnet user-secrets set "Telegram:BotToken" "ТВОЙ_ТОКЕН"
  ```
- **Переменная окружения**: `Telegram__BotToken`
- **appsettings.Development.json** (не коммить токен в репозиторий)

## Подключение своего Telegram Mini App

1. Создай своего бота в [@BotFather](https://t.me/BotFather); токен никому не пересылай и не добавляй в frontend.
2. Задеплой Mini App на HTTPS. В BotFather привяжи domain и укажи URL Mini App/menu button на frontend origin.
3. Получи свой числовой Telegram user ID для admin allowlist и создай отдельный случайный webhook secret. Bot API допускает в `secret_token` только латинские буквы, цифры, `_`, `-`; не используй bot token как webhook secret.
4. Находясь в каталоге `TusaMap.Api`, задай настройки локально через User Secrets (подставь свои значения в терминале):

    ```powershell
    dotnet user-secrets set "Telegram:BotToken" "<TOKEN_FROM_BOTFATHER>"
    dotnet user-secrets set "Telegram:WebAppUrl" "https://<your-mini-app-host>"
    dotnet user-secrets set "Telegram:WebhookUrl" "https://<your-api-host>/api/telegram/webhook"
    dotnet user-secrets set "Telegram:WebhookSecret" "<RANDOM_WEBHOOK_SECRET>"
    dotnet user-secrets set "Telegram:AdminUserIds:0" "<YOUR_NUMERIC_TELEGRAM_ID>"
    ```

5. Запусти API в `Development`: при старте он зарегистрирует Mini App menu button и webhook. Telegram не сможет обратиться к `localhost`, поэтому для локального тестирования нужен публичный HTTPS tunnel.
6. В frontend передавай исходный `Telegram.WebApp.initData` целиком в заголовке `X-Telegram-Init-Data`. Сервер проверяет HMAC подпись bot token и свежесть `auth_date`; не доверяй `initDataUnsafe`, ID или роли, присланным из UI.

В production задай те же параметры через secret/environment-variable manager хостинга (`Telegram__BotToken`, `Telegram__WebAppUrl`, `Telegram__WebhookUrl`, `Telegram__WebhookSecret`, `Telegram__AdminUserIds__0`). Никогда не коммить credentials в `appsettings*.json` или Git. `Telegram:AdminUserIds` управляет admin-only endpoints.

Для production используй стабильный HTTPS API hostname. Cloudflare Quick Tunnel выдаёт временное имя: при смене hostname обнови `Telegram:WebhookUrl` и frontend production `apiUrl`, перезапусти API и заново разверни frontend. Этот backend содержит TUSA-specific event seeder и сценарии; перед использованием как собственный starter замени бренд, TUSA event/payment defaults и модерационные правила.

## Эндпоинты

- `GET /api/events` — список мероприятий (query: `category`)
- `GET /api/events/{id}` — мероприятие по id
- `GET /api/events/{id}/going` — число отметок участия и состояние текущего пользователя
- `GET /api/analytics/summary` — сводные счётчики действий воронки, только для администратора с проверенным Telegram `initData`
- `GET /api/analytics/summary/daily` — действия по дням за последние 7 календарных дней Астаны; это counts действий, не уникальные пользователи и не посещаемость; доступ только администратору
- `GET /api/analytics/summary/insights?days=7|30|90` — админская сводка за период: дневные ряды для графиков, категории по открытиям, поисковые слова (только от 3 совпадений) и воронка промо. Поисковые слова состоят только из букв и не привязываются к Telegram ID.
- `GET /api/analytics/summary/insights?days=7|30|90` — админская сводка за период: дневные ряды для графиков, категории по открытиям, поисковые слова (только от 3 совпадений) и воронка промо. Поисковые слова состоят только из букв и не привязываются к Telegram ID.
- `GET /api/analytics/summary/meetup-density` и `/summary/meetup-campaigns` — агрегированные RSVP-планы и host-кампании, только для администратора
- `POST /api/events/{id}/going` — переключить участие; требуется проверенный Telegram `initData`
- `GET /api/organizer/engagement` — агрегированные открытия, shares и RSVP по своим одобренным событиям; требуется проверенный Telegram `initData`, admin видит все одобренные non-demo события
- `POST /api/events/{id}/ticket-categories` и `/promo-codes` — legacy endpoints отвечают `410 Gone`; TUSA не управляет ticket inventory
- `POST /api/events` — отправить мероприятие на модерацию (заголовок `X-Telegram-Init-Data`)
- `POST /api/events/public` — демо-форма заявки без Telegram-авторизации; заявка остаётся `pending`, публично не появляется и требует ручной проверки; ограничение — 5 заявок за 10 минут на API-видимый IP
- `GET /api/events/pending` — список заявок для admin Telegram ID
- `POST /api/events/{id}/approve` — одобрить событие для admin Telegram ID
- `POST /api/events/{id}/reject` — отклонить ожидающую заявку для admin Telegram ID
- `GET /api/events/{id}/going` и `POST /api/events/{id}/going` — счётчик участия и переключение RSVP для проверенного Telegram-пользователя
- `GET /api/tickets/me` — ранее оформленные/legacy записи билетов пользователя (заголовок `X-Telegram-Init-Data`); новые офлайн-билеты TUSA не продаёт
- `POST /api/tickets` и `/api/tickets/public` — отключены. TUSA не оформляет офлайн-билеты и не принимает оплату за вход.
- `POST /api/tickets/quote` — legacy endpoint закрыт (410 Gone); TUSA не рассчитывает офлайн-билеты.
- `POST /api/payments/webhook` — legacy endpoint отключён (410). Новые офлайн-продажи выключены; Stars `successful_payment` обрабатывается только для цифрового Organizer Pro.
- `POST /api/telegram/webhook` — Telegram updates; `/terms` и `/paysupport` доступны пользователям. Новые продажи офлайн-билетов отключены. Organizer Pro — цифровая услуга, для неё используется только Telegram Stars (`XTR`).
- `/start event_tusa-2026` — сообщает, что TUSA не продаёт офлайн-билеты и не принимает оплату за вход.
- `/start subscribe_pro` — показывает условия и создаёт Stars invoice Organizer Pro, только если настроена цена `Payments__TelegramSubscriptionStars`.
- `/organizerpro` — показать условия и приобрести/продлить Organizer Pro за Stars, если цена настроена.
- `GET /api/events/{id}/promotion/offer`, `POST /api/events/{id}/promotion/invoice`, `POST /api/events/{id}/promotion/cancel` — владелец одобренного события может получить условия/счёт/отменить незавершённый счёт для рекламного размещения; внешний продавец и HTTPS URL обязательны. Цена по умолчанию 500 XTR, флаг по умолчанию выключен.
- `/refundstars <Telegram_ID>` — admin-only возврат последней Organizer Pro оплаты через Telegram Stars API.
- `/refundpromotion <Campaign_ID>` — admin-only возврат оплаченной кампании продвижения; campaign ID приходит администраторам в уведомлении об оплате.

После отправки заявки администраторы получают Telegram-уведомление (если заданы bot token и `Telegram:AdminUserIds`). Очередь доступна в Mini App: `/admin/event-review`. Решения разрешены только аккаунтам из серверного allowlist; повторно обработать уже закрытую заявку нельзя.

Список интереса и RSVP — отдельные бесплатные действия; ни одно из них не является бронью или билетом. Уведомления о доступности офлайн-билетов отключены вместе с продажей.

### Граница доступа

- `GET /api/events` и `GET /api/events/{id}` предназначены для публичного чтения каталога.
- Legacy записи билетов, создание события через `POST /api/events`, organizer analytics и admin endpoints должны вызываться с raw `initData`; admin дополнительно проверяется по allowlist `Telegram:AdminUserIds`.
- `POST /api/events/public` в этом демо оставлен открытым для прототипирования и **не проверяет личность автора**. Есть базовый лимит 5 заявок за 10 минут на API-видимый IP, но перед масштабным публичным запуском добавь captcha/антиспам либо закрой маршрут. За reverse proxy настрой доверенную передачу клиентского IP; не доверяй произвольному `X-Forwarded-For`. Не используй email/username из body как доказательство владельца.
- Все новые ticket-order, quote, category и promo-code операции закрыты; legacy `telegram_provider` payments не принимаются как новые покупки.

Для production CORS также следует сузить до конкретного frontend origin вместо разрешения произвольных origin.

Для production задай:

- `ConnectionStrings__Default` — PostgreSQL connection string;
- `Telegram__BotToken` — токен бота;
- `Telegram__AdminUserIds__0` — Telegram ID администратора;
- `Payments__OrganizerProEnabled` — keep `false` until the digital benefits and full support/refund flow are implemented and verified.
- `Payments__TelegramSubscriptionStars` — Stars price for the 30-day digital Organizer Pro service; keep `0` until its benefits, terms, support, and refund cycle are approved and tested. Both this price and the feature flag must be enabled for checkout.
- `Payments__EventPromotionEnabled` — держать `false`, пока не проверены условия, поддержка/возвраты и Telegram test-cycle. `Payments__EventPromotionStars` — предложенная цена `500 XTR` за одобренное промо одного события до его даты; эквивалент USD не гарантируется.
- `Analytics__DailyReportEnabled` и `Analytics__DailyReportTimeAstana` — ежедневный агрегированный отчёт администраторам Telegram (по умолчанию 20:00 Астана). Нужны bot token и `Telegram:AdminUserIds`; графики и фильтр периода доступны отдельно в `/admin/analytics`.
- `Payments__EventPromotionEnabled` — держать `false`, пока не проверены условия, поддержка/возвраты и Telegram test-cycle. `Payments__EventPromotionStars` — предложенная цена `500 XTR` за одобренное промо одного события до его даты; эквивалент USD не гарантируется.
- `Analytics__DailyReportEnabled` и `Analytics__DailyReportTimeAstana` — ежедневный агрегированный отчёт администраторам Telegram (по умолчанию 20:00 Астана). Нужны bot token и `Telegram:AdminUserIds`; графики и фильтр периода доступны отдельно в `/admin/analytics`.
- `Telegram__WebhookSecret` — secret used when registering the Telegram webhook;
- `Cors__Origins__0` — разрешённый frontend origin.

Telegram requires Stars for digital goods and services inside bots and Mini Apps. Stars must not be used for offline event admission; that is why TUSA's physical-ticket checkout remains disabled instead of being converted to Stars. Organizer Pro may use Stars only after prior terms consent and a verified `/paysupport`/refund process. See https://core.telegram.org/bots/payments-stars and https://core.telegram.org/bots/payments.

Do not configure a physical payment-provider token for this product. Offline ticket sales are hard-disabled in code. Keep `Payments:OrganizerProEnabled=false` and the Stars price at `0` until the digital Organizer Pro package and its complete Telegram test, support, and refund lifecycle have been verified; both settings are required to open checkout.
