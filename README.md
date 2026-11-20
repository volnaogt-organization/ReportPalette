# ReportPalette

Клиент для системы [ManagerDataBase](https://github.com/volnaogt-organization/ManagerDataBase)
(обработка производственных отчётов 1010/2012 по спискам ДСЕ).

Два варианта клиента:

| Клиент | Папка | Когда использовать |
|--------|-------|--------------------|
| **WPF-приложение** (Windows) | корень репозитория | Локальная работа: глобальный хоткей (Ctrl+Alt+Q), запуск локальных программ |
| **Веб-палитра** (Docker) | `frontend/` + `backend/` | Работа с разных устройств через браузер, без установки Python |

Общий формат переноса настроек (хеш-строка RP2) — конфиг можно переносить
между WPF- и веб-версией (веб переносит секции «Приложения» и «Общие»).

## Запуск веб-версии (Docker)

```bash
# 1. Клонировать ядро (один раз)
git clone https://github.com/volnaogt-organization/ManagerDataBase backend/ManagerDataBase

# 2. Собрать и поднять оба контейнера
docker compose up -d --build

# 3. Открыть
http://localhost:8080        # палитра
http://localhost:8000/docs   # Swagger API бэкенда
```

С любого устройства в сети: `http://<адрес-сервера>:8080`.
Данные Excel/БД, к которым обращается ManagerDataBase, должны быть доступны
из контейнера бэкенда.

## Локальная разработка без Docker

- Бэкенд: см. `backend/README.md` (uvicorn на 127.0.0.1:8000).
- Фронтенд: любой статический сервер для `frontend/`, но API-запросы идут на
  `/api/...` — проще всего поднять через docker compose или открыть
  `frontend/index.html` с поправкой адреса в `app.js`.

## WPF-клиент

```bash
dotnet build
bin\Debug\net10.0-windows\HotkeyApp.exe
```

Хоткей по умолчанию Ctrl+Alt+Q, настройки — кнопка ⚙.
