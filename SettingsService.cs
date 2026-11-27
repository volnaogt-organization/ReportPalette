using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HotkeyApp
{
    /// <summary>Одна запись панели запуска: произвольное приложение.</summary>
    public class LaunchItem
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("arguments")] public string Arguments { get; set; } = "";
    }

    /// <summary>
    /// Плагин-команда: внешний исполняемый файл, интегрированный в палитру.
    /// Запускается как процесс; обмен — через аргументы ({query}) и stdout.
    /// </summary>
    public class PluginItem
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("keyword")] public string Keyword { get; set; } = "";
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        /// <summary>Чем запускать: "" — как есть (exe/bat), "python", "powershell" и т.п.</summary>
        [JsonPropertyName("runtime")] public string Runtime { get; set; } = "";
        /// <summary>Шаблон аргументов; {query} заменяется введённым текстом.</summary>
        [JsonPropertyName("argumentsTemplate")] public string ArgumentsTemplate { get; set; } = "{query}";
        /// <summary>true — stdout показывается в палитре; false — просто запуск.</summary>
        [JsonPropertyName("captureOutput")] public bool CaptureOutput { get; set; } = false;
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    }

    /// <summary>Что запускать по расписанию.</summary>
    public class ScheduledAction
    {
        /// <summary>launch (приложение) | plugin (плагин по keyword) | backend_report (отчёт 1010/2012)</summary>
        [JsonPropertyName("type")] public string Type { get; set; } = "launch";
        /// <summary>Путь/имя для launch, keyword для plugin, номер отчёта для backend_report.</summary>
        [JsonPropertyName("target")] public string Target { get; set; } = "";
        /// <summary>Аргументы запуска / query для плагина / список ДСЕ для отчёта.</summary>
        [JsonPropertyName("arguments")] public string Arguments { get; set; } = "";
    }

    /// <summary>Когда запускать задачу.</summary>
    public class ScheduledTrigger
    {
        /// <summary>once | interval | daily | weekly | startup</summary>
        [JsonPropertyName("type")] public string Type { get; set; } = "once";
        /// <summary>"HH:mm" для daily/weekly.</summary>
        [JsonPropertyName("time")] public string Time { get; set; } = "09:00";
        /// <summary>Для weekly: "Mon","Tue",... </summary>
        [JsonPropertyName("daysOfWeek")] public List<string> DaysOfWeek { get; set; } = new();
        /// <summary>Для interval.</summary>
        [JsonPropertyName("intervalMinutes")] public int IntervalMinutes { get; set; } = 60;
        /// <summary>Для once.</summary>
        [JsonPropertyName("runAt")] public DateTime? RunAt { get; set; }
    }

    /// <summary>Задача планировщика (гибрид: внутри приложения или в Планировщике Windows).</summary>
    public class ScheduledTaskItem
    {
        [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("action")] public ScheduledAction Action { get; set; } = new();
        [JsonPropertyName("trigger")] public ScheduledTrigger Trigger { get; set; } = new();
        /// <summary>app — таймер внутри приложения; windows_task_scheduler — задача Windows.</summary>
        [JsonPropertyName("runner")] public string Runner { get; set; } = "app";
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("lastRun")] public DateTime? LastRun { get; set; }
        [JsonPropertyName("notifyOnResult")] public bool NotifyOnResult { get; set; } = true;
    }

    /// <summary>Настройки пользователя, сохраняемые в JSON-файл.</summary>
    public class AppSettings
    {
        /// <summary>Директория, доступ к которой выдан через drag-and-drop.</summary>
        [JsonPropertyName("allowedDirectory")] public string AllowedDirectory { get; set; } = "";

        /// <summary>Показывать «остров» — таблетку сверху экрана для drag-and-drop отчётов.</summary>
        [JsonPropertyName("islandEnabled")] public bool IslandEnabled { get; set; } = true;

        /// <summary>Сохранённая позиция острова (null — по центру сверху).</summary>
        [JsonPropertyName("islandLeft")] public double? IslandLeft { get; set; }
        [JsonPropertyName("islandTop")] public double? IslandTop { get; set; }

        /// <summary>Версия схемы конфига. Отсутствие поля при загрузке = v1.</summary>
        [JsonPropertyName("version")] public int Version { get; set; } = 2;
        [JsonPropertyName("backendUrl")] public string BackendUrl { get; set; } = "http://127.0.0.1:8000/";
        [JsonPropertyName("hotkeyModifiers")] public string HotkeyModifiers { get; set; } = "CTRL|ALT"; // WIN|CTRL|SHIFT|ALT (через |)
        [JsonPropertyName("hotkeyKey")] public string HotkeyKey { get; set; } = "Q"; // дефолт: Ctrl+Alt+Q (Win+Space занят Windows)
        [JsonPropertyName("topmost")] public bool Topmost { get; set; } = true;
        [JsonPropertyName("stayOpenOnLostFocus")] public bool StayOpenOnLostFocus { get; set; } = false;
        [JsonPropertyName("defaultProgram")] public int DefaultProgram { get; set; } = 1010;
        [JsonPropertyName("runAtWindowsStartup")] public bool RunAtWindowsStartup { get; set; } = false;
        [JsonPropertyName("launchItems")] public List<LaunchItem> LaunchItems { get; set; } = new();
        [JsonPropertyName("plugins")] public List<PluginItem> Plugins { get; set; } = new();
        [JsonPropertyName("scheduledTasks")] public List<ScheduledTaskItem> ScheduledTasks { get; set; } = new();
    }

    /// <summary>Секции конфига для селективного переноса настроек.</summary>
    public static class SettingsSections
    {
        public const string Hotkey = "hotkey";
        public const string Window = "window";
        public const string Launch = "launch";
        public const string Plugins = "plugins";
        public const string Tasks = "tasks";
        public const string Backend = "backend";
        public const string General = "general";

        /// <summary>Все секции в каноническом порядке.</summary>
        public static readonly string[] All =
            { Hotkey, Window, Launch, Plugins, Tasks, Backend, General };

        public static string DisplayName(string section) => section switch
        {
            Hotkey => "Хоткей",
            Window => "Режим окна",
            Launch => "Панель запуска",
            Plugins => "Плагины",
            Tasks => "Задачи расписания",
            Backend => "Адрес бэкенда",
            General => "Общие настройки",
            _ => section
        };
    }

    /// <summary>
    /// Результат разбора хэш-строки: какие секции внутри и их содержимое.
    /// Передаётся в диалог предпросмотра, затем в ApplyImport.
    /// </summary>
    public class ImportPreview
    {
        public bool IsV1 { get; set; }
        public List<string> Sections { get; set; } = new();
        public JsonObject Payload { get; set; } = new();
        public int LaunchCount { get; set; }
        public int PluginCount { get; set; }
        public int TaskCount { get; set; }
        /// <summary>Есть ли в строке списковые секции (для выбора «заменить/объединить»).</summary>
        public bool HasLists => LaunchCount > 0 || PluginCount > 0 || TaskCount > 0;
    }

    /// <summary>
    /// Загрузка/сохранение конфига + генерация и разбор «хеш-строки» настроек
    /// для переноса конфигурации между машинами (копируешь строку — вставляешь в поле).
    ///
    /// Форматы строк:
    ///   v1 (legacy): Base64(полный JSON) . sha256-8
    ///   v2:          RP2 . секции,через,запятую . Base64(JSON с выбранными полями) . sha256-8
    /// v1-строки принимаются на импорт и трактуются как «все секции».
    /// </summary>
    public static class SettingsService
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static string ConfigPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "HotkeyApp", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                    if (s != null)
                    {
                        // миграция v1 -> v2: отсутствие version означает старый формат;
                        // новые списки просто останутся пустыми дефолтами
                        if (s.Version < 2)
                        {
                            s.Version = 2;
                            try { Save(s); } catch { }
                        }
                        return s;
                    }
                }
            }
            catch { /* повреждённый конфиг — начинаем с дефолтов */ }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(settings, JsonOpts));
        }

        // ---------- Перенос настроек v2: выбор секций ----------

        /// <summary>Генерирует переносимую строку настроек только по выбранным секциям.</summary>
        public static string ExportHashString(AppSettings settings, IEnumerable<string> sections)
        {
            var sectionSet = new HashSet<string>(sections);
            var payload = new JsonObject();

            if (sectionSet.Contains(SettingsSections.Hotkey))
            {
                payload["hotkeyModifiers"] = settings.HotkeyModifiers;
                payload["hotkeyKey"] = settings.HotkeyKey;
            }
            if (sectionSet.Contains(SettingsSections.Window))
            {
                payload["topmost"] = settings.Topmost;
                payload["stayOpenOnLostFocus"] = settings.StayOpenOnLostFocus;
                payload["islandEnabled"] = settings.IslandEnabled;
            }
            if (sectionSet.Contains(SettingsSections.Launch))
                payload["launchItems"] = JsonSerializer.SerializeToNode(settings.LaunchItems);
            if (sectionSet.Contains(SettingsSections.Plugins))
                payload["plugins"] = JsonSerializer.SerializeToNode(settings.Plugins);
            if (sectionSet.Contains(SettingsSections.Tasks))
                payload["scheduledTasks"] = JsonSerializer.SerializeToNode(settings.ScheduledTasks);
            if (sectionSet.Contains(SettingsSections.Backend))
                payload["backendUrl"] = settings.BackendUrl;
            if (sectionSet.Contains(SettingsSections.General))
            {
                payload["defaultProgram"] = settings.DefaultProgram;
                payload["runAtWindowsStartup"] = settings.RunAtWindowsStartup;
            }

            var json = payload.ToJsonString();
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            var checksum = Sha256Short(b64);
            var ordered = SettingsSections.All.Where(sectionSet.Contains);
            return $"RP2.{string.Join(",", ordered)}.{b64}.{checksum}";
        }

        /// <summary>Экспорт всех секций (совместимость со старым поведением).</summary>
        public static string ExportHashString(AppSettings settings) =>
            ExportHashString(settings, SettingsSections.All);

        /// <summary>
        /// Разбирает строку переноса (v1 или v2), проверяет хеш и возвращает
        /// предпросмотр секций. null — если строка повреждена.
        /// </summary>
        public static ImportPreview? PreviewImport(string hashString, out string error)
        {
            error = "";
            hashString = (hashString ?? "").Trim();
            if (hashString.Length == 0)
            {
                error = "Пустая строка.";
                return null;
            }

            if (hashString.StartsWith("RP2.", StringComparison.Ordinal))
                return PreviewV2(hashString, out error);
            return PreviewV1(hashString, out error);
        }

        private static ImportPreview? PreviewV2(string hashString, out string error)
        {
            error = "";
            // base64 не содержит точек, поэтому разбиение по '.' безопасно
            var parts = hashString.Split('.');
            if (parts.Length != 4)
            {
                error = "Неверный формат строки (ожидалось: RP2.секции.Base64.Хеш).";
                return null;
            }

            var sections = parts[1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => SettingsSections.All.Contains(s))
                .ToList();
            if (sections.Count == 0)
            {
                error = "В строке нет ни одной известной секции.";
                return null;
            }

            var payload = parts[2];
            if (Sha256Short(payload) != parts[3])
            {
                error = "Контрольная хеш-сумма не совпала — строка повреждена.";
                return null;
            }

            try
            {
                var node = JsonNode.Parse(
                    Encoding.UTF8.GetString(Convert.FromBase64String(payload))) as JsonObject;
                if (node == null)
                {
                    error = "Не удалось прочитать настройки из строки.";
                    return null;
                }
                return BuildPreview(node, sections, isV1: false);
            }
            catch (Exception ex)
            {
                error = $"Ошибка разбора строки: {ex.Message}";
                return null;
            }
        }

        private static ImportPreview? PreviewV1(string hashString, out string error)
        {
            error = "";
            var dot = hashString.LastIndexOf('.');
            if (dot <= 0 || dot == hashString.Length - 1)
            {
                error = "Неверный формат строки (ожидалось: RP2.секции.Base64.Хеш или Base64.Хеш).";
                return null;
            }

            var payload = hashString[..dot];
            var checksum = hashString[(dot + 1)..];
            if (Sha256Short(payload) != checksum)
            {
                error = "Контрольная хеш-сумма не совпала — строка повреждена.";
                return null;
            }

            try
            {
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (settings == null)
                {
                    error = "Не удалось прочитать настройки из строки.";
                    return null;
                }
                // v1 содержал все настройки — собираем эквивалентный полный payload
                var node = JsonSerializer.SerializeToNode(settings, JsonOpts)!.AsObject();
                node.Remove("version");
                return BuildPreview(node, SettingsSections.All.ToList(), isV1: true);
            }
            catch (Exception ex)
            {
                error = $"Ошибка разбора строки: {ex.Message}";
                return null;
            }
        }

        private static ImportPreview BuildPreview(JsonObject payload, List<string> sections, bool isV1)
        {
            return new ImportPreview
            {
                IsV1 = isV1,
                Sections = sections,
                Payload = payload,
                LaunchCount = payload["launchItems"] is JsonArray la ? la.Count : 0,
                PluginCount = payload["plugins"] is JsonArray pl ? pl.Count : 0,
                TaskCount = payload["scheduledTasks"] is JsonArray t ? t.Count : 0,
            };
        }

        /// <summary>
        /// Применяет предпросмотренный импорт к текущему конфигу и сохраняет его.
        /// mergeLists: true — объединить списки (приложения по имени, плагины по имени,
        /// задачи по id), входящие записи имеют приоритет; false — заменить списки целиком.
        /// </summary>
        public static bool ApplyImport(ImportPreview preview, bool mergeLists, out string error)
        {
            error = "";
            try
            {
                var settings = Load();
                var p = preview.Payload;

                if (p["hotkeyModifiers"] is JsonValue hm && hm.TryGetValue<string>(out var hmS))
                    settings.HotkeyModifiers = hmS;
                if (p["hotkeyKey"] is JsonValue hk && hk.TryGetValue<string>(out var hkS))
                    settings.HotkeyKey = hkS;
                if (p["topmost"] is JsonValue tm && tm.TryGetValue<bool>(out var tmB))
                    settings.Topmost = tmB;
                if (p["stayOpenOnLostFocus"] is JsonValue so && so.TryGetValue<bool>(out var soB))
                    settings.StayOpenOnLostFocus = soB;
                if (p["islandEnabled"] is JsonValue ie && ie.TryGetValue<bool>(out var ieB))
                    settings.IslandEnabled = ieB;
                if (p["backendUrl"] is JsonValue bu && bu.TryGetValue<string>(out var buS))
                    settings.BackendUrl = buS;
                if (p["defaultProgram"] is JsonValue dp && dp.TryGetValue<int>(out var dpI))
                    settings.DefaultProgram = dpI;
                if (p["runAtWindowsStartup"] is JsonValue rs && rs.TryGetValue<bool>(out var rsB))
                    settings.RunAtWindowsStartup = rsB;

                if (p["launchItems"] is JsonArray la)
                {
                    var incoming = la.Deserialize<List<LaunchItem>>(JsonOpts) ?? new();
                    settings.LaunchItems = mergeLists
                        ? MergeByName(settings.LaunchItems, incoming, x => x.Name)
                        : incoming;
                }
                if (p["plugins"] is JsonArray pl)
                {
                    var incoming = pl.Deserialize<List<PluginItem>>(JsonOpts) ?? new();
                    settings.Plugins = mergeLists
                        ? MergeByName(settings.Plugins, incoming, x => x.Name)
                        : incoming;
                }
                if (p["scheduledTasks"] is JsonArray st)
                {
                    var incoming = st.Deserialize<List<ScheduledTaskItem>>(JsonOpts) ?? new();
                    settings.ScheduledTasks = mergeLists
                        ? MergeByName(settings.ScheduledTasks, incoming, x => x.Id)
                        : incoming;
                }

                Save(settings);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Ошибка применения настроек: {ex.Message}";
                return false;
            }
        }

        /// <summary>Объединение списков: совпадения по ключу перезаписываются входящими.</summary>
        private static List<T> MergeByName<T>(List<T> current, List<T> incoming, Func<T, string> key)
        {
            var result = new List<T>(current);
            foreach (var item in incoming)
            {
                var idx = result.FindIndex(x =>
                    string.Equals(key(x), key(item), StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) result[idx] = item;
                else result.Add(item);
            }
            return result;
        }

        private static string Sha256Short(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            // первые 8 байт => 16 hex-символов — достаточно для контроля целостности
            return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
        }
    }
}
