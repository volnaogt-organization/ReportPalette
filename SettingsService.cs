using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    /// <summary>Настройки пользователя, сохраняемые в JSON-файл.</summary>
    public class AppSettings
    {
        [JsonPropertyName("backendUrl")] public string BackendUrl { get; set; } = "http://127.0.0.1:8000/";
        [JsonPropertyName("hotkeyModifiers")] public string HotkeyModifiers { get; set; } = "CTRL|ALT"; // WIN|CTRL|SHIFT|ALT (через |)
        [JsonPropertyName("hotkeyKey")] public string HotkeyKey { get; set; } = "Q"; // дефолт: Ctrl+Alt+Q (Win+Space занят Windows)
        [JsonPropertyName("topmost")] public bool Topmost { get; set; } = true;
        [JsonPropertyName("stayOpenOnLostFocus")] public bool StayOpenOnLostFocus { get; set; } = false;
        [JsonPropertyName("defaultProgram")] public int DefaultProgram { get; set; } = 1010;
        [JsonPropertyName("launchItems")] public List<LaunchItem> LaunchItems { get; set; } = new();
    }

    /// <summary>
    /// Загрузка/сохранение конфига + генерация и разбор «хеш-строки» настроек
    /// для переноса конфигурации между машинами (копируешь строку — вставляешь в поле).
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
                    if (s != null) return s;
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

        // ---------- Перенос настроек: строка base64(json) + контрольная сумма ----------

        /// <summary>Генерирует переносимую строку настроек: Base64(JSON) + '.' + короткий хеш SHA256.</summary>
        public static string ExportHashString(AppSettings settings)
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions());
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            var checksum = Sha256Short(payload);
            return $"{payload}.{checksum}";
        }

        /// <summary>
        /// Разбирает строку переноса, проверяет хеш и сохраняет настройки в конфиг.
        /// Возвращает false, если строка повреждена или не прошла проверку.
        /// </summary>
        public static bool ImportHashString(string hashString, out string error)
        {
            error = "";
            try
            {
                hashString = hashString.Trim();
                var dot = hashString.LastIndexOf('.');
                if (dot <= 0 || dot == hashString.Length - 1)
                {
                    error = "Неверный формат строки (ожидалось: Base64.Хеш).";
                    return false;
                }

                var payload = hashString[..dot];
                var checksum = hashString[(dot + 1)..];

                if (Sha256Short(payload) != checksum)
                {
                    error = "Контрольная хеш-сумма не совпала — строка повреждена.";
                    return false;
                }

                var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (settings == null)
                {
                    error = "Не удалось прочитать настройки из строки.";
                    return false;
                }

                Save(settings);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Ошибка разбора строки: {ex.Message}";
                return false;
            }
        }

        private static string Sha256Short(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            // первые 8 байт => 16 hex-символов — достаточно для контроля целостности
            return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
        }
    }
}
