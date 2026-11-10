# Публичный backend с личного компьютера

## Важное ограничение

`localhost` доступен только на твоём компьютере. GitHub Pages и Telegram не смогут обратиться к нему напрямую.

Для публичной работы нужен:

- компьютер включён и не уходит в сон;
- стабильный HTTPS-адрес, например `https://api.example.kz`;
- Cloudflare Tunnel или другой HTTPS-туннель;
- backend на `localhost:5001`;
- frontend, собранный с публичным API URL.

## Рекомендуемый вариант: Cloudflare Tunnel

1. Зарегистрируй домен или используй домен, которым уже владеешь.
2. Создай аккаунт Cloudflare и добавь домен.
3. Установи `cloudflared` с официальной страницы:
   https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/
4. Выполни вход:

```powershell
cloudflared tunnel login
```

5. Создай именованный туннель:

```powershell
cloudflared tunnel create tusa-api
cloudflared tunnel route dns tusa-api api.example.kz
```

6. Создай `%USERPROFILE%\.cloudflared\config.yml`:

```yaml
tunnel: tusa-api
credentials-file: C:\Users\YOUR_USER\.cloudflared\TUNNEL_ID.json

ingress:
  - hostname: api.example.kz
    service: http://localhost:5001
  - service: http_status:404
```

7. Запусти backend и tunnel:

```powershell
$env:TUSA_PUBLIC_API_URL = 'https://api.example.kz'
.\start-public.ps1
```

## Frontend

В `src/environments/environment.prod.ts` нужно указать:

```ts
apiUrl: 'https://api.example.kz'
```

После этого пересобрать и запушить frontend. GitHub Pages будет обращаться к backend через HTTPS.

## CORS

Backend уже разрешает `https://tusa-astana.github.io` (и старый `https://yessken.github.io`). Если появится другой frontend-домен, добавь его через переменную:

```powershell
$env:Cors__Origins__2 = 'https://another-frontend.example'
```

## Без домена: только временный тест

Можно запустить:

```powershell
cloudflared tunnel --url http://localhost:5001
```

Cloudflare выдаст случайный `trycloudflare.com` URL. Он подходит для короткого теста, но не для production: адрес меняется, процесс нужно держать запущенным, а frontend нужно пересобрать с этим URL.

## Что будет работать через публичный tunnel

- события;
- категории билетов;
- заказы;
- Telegram auth;
- webhook оплаты, если платёжный провайдер сможет обратиться к публичному endpoint;
- доставка билета ботом;
- check-in QR.

Не будет работать, если компьютер выключен, уснул, потерял интернет или остановлен `cloudflared`.
