# ReportPalette

Модульная палитра отчётов. Каждый модуль —
[ManagerDataBase](https://github.com/volnaogt-organization/ManagerDataBase)
(обработка производственных отчётов 1010/2012 по спискам ДСЕ) и будущие
программы — работает отдельным docker-контейнером со своим API.
ReportPalette — оболочка над ними: шлюз + клиенты.

```
┌─────────────┐   ┌──────────────┐        ┌─────────────────────────┐
│ WPF-клиент  │──►│              │───────►│ модуль managerdatabase  │
└─────────────┘   │   Шлюз       │        │ (контейнер из GHCR)     │
┌─────────────┐   │  (FastAPI)   │        └─────────────────────────┘
│ Веб-палитра │──►│              │───────►│ будущий модуль …        │
└─────────────┘   └──────┬───────┘        └─────────────────────────┘
                         │ Docker socket
                         ▼
              обновление модулей из релизов
              GitHub по кнопке в интерфейсе
```

Два варианта клиента:

| Клиент | Папка | Когда использовать |
|--------|-------|--------------------|
| **WPF-приложение** (Windows) | корень репозитория | Локальная работа: глобальный хоткей (Ctrl+Alt+Q), запуск локальных программ |
| **Веб-палитра** (Docker) | `frontend/` + `backend/` | Работа с разных устройств через браузер, без установки Python |

Общий формат переноса настроек (хеш-строка RP2) — конфиг можно переносить
между WPF- и веб-версией (веб переносит секции «Приложения» и «Общие»).

## Запуск веб-версии (Docker)

```bash
docker compose up -d --build

http://localhost:8080        # палитра
http://localhost:8000/docs   # Swagger API шлюза
```

С любого устройства в сети: `http://<адрес-сервера>:8080`.

Образ модуля ManagerDataBase берётся из GHCR
(`ghcr.io/volnaogt-organization/managerdatabase:latest`). Если пакет приватный,
один раз на сервере: `docker login ghcr.io`.

## Обновление модулей

В веб-палитре: ⚙ → «Модули» → видно текущую версию каждого модуля и последний
релиз на GitHub; кнопка **«Обновить»** скачивает новый образ и перезапускает
контейнер модуля.

Релиз модуля делается в его репозитории тегом: `git tag v0.5.3 && git push origin v0.5.3` —
GitHub Actions собирает образ в GHCR и создаёт Release
(настройка для ManagerDataBase — в `module-kit/managerdatabase/`).

## Разработка модуля из локальных исходников

```bash
git clone https://github.com/volnaogt-organization/ManagerDataBase backend/ManagerDataBase
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build
```

## Добавление нового модуля

1. Шаблон сервисной обёртки и релизного workflow: `module-kit/managerdatabase/`.
2. Сервис в `docker-compose.yml` + запись в `backend/modules.json`.
3. Подробности — в `backend/README.md`.

## Локальная разработка шлюза без Docker

См. `backend/README.md` (uvicorn на 127.0.0.1:8000).

## WPF-клиент

```bash
dotnet build
bin\Debug\net10.0-windows\HotkeyApp.exe
```

Хоткей по умолчанию Ctrl+Alt+Q, настройки — кнопка ⚙. Клиент обращается
к шлюзу (`BackendClient.cs`); старые маршруты `/run`, `/annual_plan`
шлюз проксирует модулю ManagerDataBase.
