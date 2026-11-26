using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HotkeyApp
{
    /// <summary>
    /// Команда палитры, объявленная модулем в module.json (ui.commands).
    /// Палитра не знает про конкретные отчёты — что умеет модуль, то и в списке.
    /// </summary>
    public class ModuleCommand
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("hint")] public string? Hint { get; set; }

        /// <summary>Тип ввода: dse_list (список ДСЕ через $), file (путь к файлу), none.</summary>
        [JsonPropertyName("input")] public string Input { get; set; } = "none";

        /// <summary>Фильтр диалога выбора файла, напр. "Excel|*.xlsx;*.xls".</summary>
        [JsonPropertyName("filter")] public string? Filter { get; set; }

        [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";
        [JsonPropertyName("payload")] public Dictionary<string, JsonElement>? Payload { get; set; }

        /// <summary>Команда по умолчанию для «сырого» ввода ДСЕ без выбора команды.</summary>
        [JsonPropertyName("default")] public bool Default { get; set; }

        [JsonIgnore] public string ModuleName { get; set; } = "";
        [JsonIgnore] public string Key => ModuleName + ":" + Id;

        /// <summary>file — путь к файлу (excel_file — устаревший синоним).</summary>
        [JsonIgnore] public bool IsFileInput => Input == "file" || Input == "excel_file";

        public bool IsValid => Id.Length > 0 && Title.Length > 0 && Endpoint.Length > 0;

        public Dictionary<string, object?> BuildPayload(IReadOnlyList<string>? dseList, string? filePath)
        {
            var dict = new Dictionary<string, object?>();
            if (Payload != null)
                foreach (var kv in Payload)
                    dict[kv.Key] = kv.Value; // JsonElement сериализуется как есть
            if (dseList != null) dict["dse_list"] = dseList;
            if (filePath != null) dict["path_to_excel_file"] = filePath;
            return dict;
        }
    }

    /// <summary>Читает команды из module.json установленных модулей.</summary>
    public static class ModuleCatalog
    {
        public static List<ModuleCommand> LoadCommands()
        {
            var result = new List<ModuleCommand>();
            foreach (var def in ModuleManager.LoadDefs())
            {
                var dir = ModuleManager.GetInstalledAppDir(def.Name);
                if (dir == null) continue;

                var manifestPath = Path.Combine(dir, "module.json");
                if (!File.Exists(manifestPath)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                    if (!doc.RootElement.TryGetProperty("ui", out var ui)) continue;
                    if (!ui.TryGetProperty("commands", out var cmds)) continue;

                    foreach (var el in cmds.EnumerateArray())
                    {
                        var cmd = JsonSerializer.Deserialize<ModuleCommand>(el.GetRawText());
                        if (cmd != null && cmd.IsValid)
                        {
                            cmd.ModuleName = def.Name;
                            result.Add(cmd);
                        }
                    }
                }
                catch { /* битый манифест модуля — пропускаем модуль */ }
            }
            return result;
        }
    }

    /// <summary>
    /// История использования команд: частые и последние.
    /// Хранится в %LOCALAPPDATA%\ReportPalette\command-history.json
    /// </summary>
    public static class CommandHistory
    {
        private class Usage
        {
            [JsonPropertyName("count")] public int Count { get; set; }
            [JsonPropertyName("last")] public DateTime LastUsed { get; set; }
        }

        private static readonly string FilePath =
            Path.Combine(ModuleManager.RootDir, "command-history.json");

        private static Dictionary<string, Usage>? _cache;

        private static Dictionary<string, Usage> Load()
        {
            if (_cache != null) return _cache;
            try
            {
                _cache = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, Usage>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch { _cache = new(); }
            return _cache;
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(ModuleManager.RootDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_cache));
            }
            catch { }
        }

        public static void Register(string key)
        {
            var map = Load();
            if (!map.TryGetValue(key, out var u))
                map[key] = u = new Usage();
            u.Count++;
            u.LastUsed = DateTime.Now;
            Save();
        }

        /// <summary>Ключи команд: сначала последние, затем частые, без повторов.</summary>
        public static List<string> GetSuggested(int recentCount = 5, int frequentCount = 3)
        {
            var map = Load();
            var keys = map.OrderByDescending(kv => kv.Value.LastUsed)
                          .Take(recentCount)
                          .Select(kv => kv.Key)
                          .ToList();
            keys.AddRange(map.OrderByDescending(kv => kv.Value.Count)
                             .Select(kv => kv.Key)
                             .Where(k => !keys.Contains(k))
                             .Take(frequentCount));
            return keys;
        }
    }
}
