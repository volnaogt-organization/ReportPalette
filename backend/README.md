# Backend (Python) для ReportPalette / ManagerDataBase

Это HTTP-сервис на **FastAPI**, который связывает клиенты ReportPalette
(WPF-приложение и веб-палитру) с логикой из репозитория
[ManagerDataBase](https://github.com/volnaogt-organization/ManagerDataBase).

## Запуск в Docker (рекомендуется)

```bash
# из корня репозитория
git clone https://github.com/volnaogt-organization/ManagerDataBase backend/ManagerDataBase
docker compose up -d --build
```

Образ включает все зависимости, включая ODBC Driver 18 for SQL Server
(нужен pyodbc/sqlalchemy из ManagerDataBase).

## Как это работает

```
Клиент (WPF / веб)  --HTTP JSON-->  FastAPI (main.py)  --import-->  ManagerDataBase (scr/...)
   BackendClient.cs / app.js          /run, /annual_plan             MainLogic.main(...)
```

Репозиторий ManagerDataBase склонирован в папку `ManagerDataBase/` рядом с `main.py`,
и `main.py` добавляет его в `sys.path`, поэтому весь код (`scr.manager_database.MainLogic`
и т.д.) доступен как обычная Python-модульная библиотека — без запуска консольного меню `cmd_body.py`.

## Локальный запуск (без Docker)

```bash
cd backend
python -m venv venv
# Windows:
venv\Scripts\activate
# Linux/macOS:
source venv/bin/activate

pip install -r requirements.txt
# зависимости самого ManagerDataBase (чтение/запись Excel, если ещё не стоят):
pip install openpyxl

uvicorn main:app --host 127.0.0.1 --port 8000
```

Интерактивная документация API: http://127.0.0.1:8000/docs

## Эндпоинты

| Метод | Путь           | Назначение                                                        |
|-------|----------------|-------------------------------------------------------------------|
| GET   | `/health`      | Проверка, что сервис жив и ядро ManagerDataBase импортировано     |
| POST  | `/run`         | Обработка списка ДСЕ отчётом 1010 или 2012                        |
| POST  | `/annual_plan` | Загрузка годового отчёта из Excel-файла                           |

Пример запроса:

```bash
curl -X POST http://127.0.0.1:8000/run \
  -H "Content-Type: application/json" \
  -d '{"program": 1010, "dse_list": ["ЛТИЯ.716713.001", "ЛТИЯ.712341.004"], "save_input": 1}'
```

## Обновление кода ManagerDataBase

```bash
cd backend/ManagerDataBase && git pull
```

(папка `ManagerDataBase/` — отдельный git-клон, в основной репозиторий она не входит)

## Связь с WPF-клиентом

На стороне клиента всё в `BackendClient.cs`: он шлёт запросы на
`http://127.0.0.1:8000`. Если бэкенд работает на другой машине — поменяйте
`BaseAddress` в `BackendClient.cs`. В `MainWindow.xaml.cs` по Enter в текстовом
поле список ДСЕ (через `$`) отправляется в `POST /run`.
