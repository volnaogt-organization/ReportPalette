# ReportPalette

Модульная палитра отчётов для Windows (WPF). Модули —
[ManagerDataBase](https://github.com/volnaogt-organization/ManagerDataBase)
(обработка производственных отчётов 1010/2012 по спискам ДСЕ) и будущие
программы — скачиваются из релизов GitHub и запускаются локально,
самим приложением. Отдельный сайт или сервер не нужны.

```
ReportPalette.exe (WPF)
   │  при запуске и по кнопке «Обновить»:
   │  GitHub Releases → скачивание модуля (zip релиза)
   ▼
%LOCALAPPDATA%\ReportPalette\
   runtime\python\            портабельный Python (скачивается один раз)
   modules\managerdatabase\   код модуля + зависимости
   ▼
модуль работает локальным сервисом на 127.0.0.1:8100,
палитра общается с ним по HTTP
```

## Запуск из исходников

```bash
dotnet build
bin\Debug\net10.0-windows\HotkeyApp.exe
```

Хоткей по умолчанию Ctrl+Alt+Q, настройки — кнопка ⚙.

## Публикация для других компьютеров

```bash
# Один самодостаточный exe (.NET встроен, устанавливать ничего не надо):
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true

# Результат:
bin\Release\net10.0-windows\win-x64\publish\HotkeyApp.exe  (+ modules.json)
```

Скопируйте `HotkeyApp.exe` и `modules.json` на целевой компьютер.

При первом запуске приложение само скачает портабельный Python (~15 МБ) и
последний релиз ManagerDataBase — нужен только доступ к github.com и
python.org. Дальше модуль обновляется кнопкой в ⚙ → **Модули**.

**Требование на целевых ПК:** Microsoft ODBC Driver 18 for SQL Server
(для подключений ManagerDataBase к базе, как и у консольной версии).

## Как устроены модули

- Реестр модулей — `modules.json` рядом с exe: имя, GitHub-репозиторий, порт,
  точка входа. Новый модуль добавляется записью в этот файл, без пересборки
  (своя кнопка/команда в палитре — уже кодом).
- Модуль — репозиторий с `Dockerfile`-независимой обёрткой `service/api.py`
  (FastAPI) и `requirements.txt`. Готовый шаблон — `module-kit/managerdatabase/`,
  его нужно один раз скопировать в репозиторий модуля (инструкция внутри).
- Релиз модуля — git-тег `v*` в его репозитории: workflow создаёт GitHub
  Release, а палитра скачивает исходники релиза и поднимает сервис локально.
- Лог модуля: `%LOCALAPPDATA%\ReportPalette\modules\<имя>\logs\module.log`.

## Веб-вариант (опционально, не нужен для WPF)

Папки `frontend/`, `backend/`, `docker-compose*.yml` — отдельный вариант
для работы через браузер с общим сервером (шлюз + модули в Docker).
Для WPF-сценария они не используются, см. `backend/README.md`.

## Перенос настроек

Хеш-строка RP2 (⚙ → «Перенос настроек») совместима между машинами:
хоткей, режим окна, панель запуска, плагины, задачи.
