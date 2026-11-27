# Backend (шлюз) ReportPalette

FastAPI-шлюз модульной системы ReportPalette. Ядро ManagerDataBase сюда
**больше не клонируется** — каждый модуль (ManagerDataBase и будущие) работает
отдельным docker-контейнером со своим HTTP API, а шлюз:

- ведёт реестр модулей (`modules.json`);
- проксирует запросы клиентов к модулям;
- проверяет релизы модулей на GitHub и **обновляет контейнеры по кнопке из интерфейса**
  (скачивает свежий образ из GHCR и пересоздаёт контейнер через Docker-сокет).

```
Клиент (WPF / веб) --HTTP--> Шлюз (main.py) --внутр. docker-сеть--> модуль (managerdatabase:8000)
                                  |
                                  +-- Docker socket: pull + recreate при обновлении
```

## Запуск в Docker (рекомендуется)

```bash
# из корня репозитория
docker compose up -d --build
```

## Реестр модулей (`modules.json`)

```json
{
  "modules": [
    {
      "name": "managerdatabase",              // уникальный идентификатор
      "display_name": "ManagerDataBase",      // подпись в интерфейсе
      "description": "…",
      "repo": "volnaogt-organization/ManagerDataBase",          // откуда брать релизы
      "image": "ghcr.io/volnaogt-organization/managerdatabase", // образ модуля
      "container": "reportpalette-mdb",       // имя контейнера (container_name в compose)
      "service": "managerdatabase",           // имя сервиса в docker-сети
      "port": 8000,
      "legacy_routes": ["/run", "/annual_plan"], // старые маршруты шлюза -> этот модуль
      "enabled": true
    }
  ]
}
```

Файл смонтирован в контейнер шлюза (`./backend/modules.json:/app/modules.json:ro`),
поэтому правки применяются без пересборки: `docker compose restart backend`.

## Эндпоинты

| Метод | Путь | Назначение |
|-------|------|-----------|
| GET  | `/health` | Статус шлюза и модулей |
| GET  | `/modules` | Модули: версия работающая / последний релиз / есть ли обновление |
| POST | `/modules/update` | Обновить все модули, для которых есть свежий релиз |
| POST | `/modules/{name}/update` | Обновить конкретный модуль |
| ANY  | `/modules/{name}/proxy/{path}` | Универсальный прокси к API модуля |
| POST | `/run`, `/annual_plan` | Совместимость со старыми клиентами (прокси к модулю из `legacy_routes`) |

Swagger: http://127.0.0.1:8000/docs

## Локальный запуск (без Docker)

```bash
cd backend
python -m venv venv && venv\Scripts\activate   # Windows
pip install -r requirements.txt
uvicorn main:app --host 127.0.0.1 --port 8000
```

Шлюз стартует и без Docker-сокета, но обновление модулей и точный статус
контейнеров будут недоступны (прокси к модулям работает).

Переменные окружения:

| Переменная | Назначение |
|-----------|-----------|
| `GITHUB_TOKEN` | Опционально. Токен для GitHub API (приватные репозитории, лимит 60→5000 запр/ч) |
| `MODULES_FILE` | Путь к `modules.json` (по умолчанию рядом с `main.py`) |

## Как добавить новый модуль

1. В репозитории модуля: `Dockerfile` + `service/api.py` + `module.json` +
   workflow релиза — готовый шаблон в `module-kit/managerdatabase/`.
2. Здесь: сервис в `docker-compose.yml` (с явным `container_name`).
3. Запись в `backend/modules.json` + `docker compose restart backend`.
4. Первый релиз: `git tag v0.1.0 && git push origin v0.1.0` в репозитории модуля.
