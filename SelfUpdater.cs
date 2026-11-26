using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HotkeyApp
{
    /// <summary>
    /// Самообновление ReportPalette по той же схеме, что и обновление модулей:
    /// последний релиз GitHub → zip-ассет с собранным приложением → распаковка
    /// во временную папку → внешний updater.bat дожидается завершения процесса,
    /// копирует файлы поверх установки и запускает программу снова.
    ///
    /// Запущенный exe не может подменить сам себя, поэтому подмену делает
    /// внешний скрипт после выхода приложения.
    ///
    /// Релиз собирается GitHub Actions (.github/workflows/release.yml) по тегу v*:
    /// ассет ReportPalette-vX.Y.Z-win-x64.zip (portable, распаковал — работает).
    /// Настройки (%APPDATA%) и модули (%LOCALAPPDATA%\ReportPalette) обновление
    /// не затрагивает.
    /// </summary>
    public static class SelfUpdater
    {
        public const string Repo = "volnaogt-organization/ReportPalette";

        /// <summary>Префикс имени zip-ассета с программой в релизе.</summary>
        private const string AssetPrefix = "ReportPalette";

        /// <summary>Версия текущей сборки (из тега релиза при сборке в CI).</summary>
        public static string CurrentVersion { get; } = DetectVersion();

        private static string DetectVersion()
        {
            try
            {
                var info = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
                if (!string.IsNullOrEmpty(info))
                    return info.Split('+')[0].TrimStart('v', 'V'); // отрезаем +commitSha
            }
            catch { }
            return "0.0.0";
        }

        public class ReleaseInfo
        {
            public string? Tag { get; set; }
            public string? AssetUrl { get; set; }
            public string? PageUrl { get; set; }

            /// <summary>Есть релиз новее текущей версии, и в нём есть zip с программой.</summary>
            public bool UpdateAvailable =>
                Tag != null && AssetUrl != null &&
                ModuleManager.CompareVersions(Tag, CurrentVersion) > 0;
        }

        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            var h = new HttpClient();
            h.DefaultRequestHeaders.UserAgent.ParseAdd("ReportPalette/1.0"); // GitHub API требует User-Agent
            h.Timeout = TimeSpan.FromMinutes(15); // self-contained zip весит ~80 МБ
            return h;
        }

        /// <summary>Проверяет последний релиз ReportPalette на GitHub.</summary>
        public static async Task<ReleaseInfo> CheckAsync(CancellationToken ct = default)
        {
            var info = new ReleaseInfo();
            try
            {
                var resp = await Http.GetAsync(
                    $"https://api.github.com/repos/{Repo}/releases/latest", ct);
                if (!resp.IsSuccessStatusCode) return info;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                info.Tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                info.PageUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;

                if (root.TryGetProperty("assets", out var assets))
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (name != null &&
                            name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) &&
                            name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            info.AssetUrl = a.GetProperty("browser_download_url").GetString();
                            break;
                        }
                    }
            }
            catch { /* нет сети/GitHub — обновления нет */ }
            return info;
        }

        /// <summary>
        /// Скачивает релиз, распаковывает, запускает внешний updater и завершает
        /// приложение. Updater подменяет файлы программы и запускает её снова.
        /// </summary>
        public static async Task ApplyAndRestartAsync(
            ReleaseInfo rel, IProgress<string>? progress, CancellationToken ct = default)
        {
            if (rel.AssetUrl == null || rel.Tag == null)
                throw new InvalidOperationException(
                    "В последнем релизе нет zip-файла с программой. " +
                    "Релиз должен собираться workflow release.yml по тегу v*.");

            var updatesDir = Path.Combine(ModuleManager.RootDir, "updates");
            Directory.CreateDirectory(updatesDir);
            var staging = Path.Combine(updatesDir, "app-" + rel.Tag.TrimStart('v', 'V'));
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            // 1. Скачивание и распаковка
            progress?.Report($"Скачивание ReportPalette {rel.Tag}…");
            var zipPath = Path.Combine(updatesDir, "app-" + rel.Tag + ".zip");
            var bytes = await Http.GetByteArrayAsync(rel.AssetUrl, ct);
            await File.WriteAllBytesAsync(zipPath, bytes, ct);

            progress?.Report("Распаковка…");
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    var dest = Path.Combine(staging,
                        entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    if (entry.FullName.EndsWith('/'))
                    {
                        Directory.CreateDirectory(dest);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }
            try { File.Delete(zipPath); } catch { }

            // защита от «пустого» релиза: в пакете обязан быть exe программы
            var exeName = Path.GetFileName(Environment.ProcessPath ?? "HotkeyApp.exe");
            if (!File.Exists(Path.Combine(staging, exeName)))
                throw new InvalidOperationException(
                    $"В скачанном пакете нет {exeName} — обновление отменено, ничего не изменено.");

            // 2. Скрипт-установщик: ждёт выхода программы, копирует файлы, перезапускает
            var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var exePath = Environment.ProcessPath ?? Path.Combine(appDir, exeName);
            var logPath = Path.Combine(updatesDir, "selfupdate.log");
            var batPath = Path.Combine(updatesDir, "selfupdate.bat");

            await File.WriteAllTextAsync(batPath, $$"""
                @echo off
                rem ReportPalette self-update: дождаться выхода, подменить файлы, перезапустить
                set "PID=%~1"
                set "SRC=%~2"
                set "DST=%~3"
                set "EXE=%~4"
                set "LOG=%~5"
                echo [%date% %time%] waiting for PID %PID% >> "%LOG%"
                :wait
                tasklist /FI "PID eq %PID%" 2>nul | findstr /R /C:" %PID% " >nul
                if not errorlevel 1 (
                    timeout /t 1 /nobreak >nul
                    goto wait
                )
                echo [%date% %time%] copying "%SRC%" to "%DST%" >> "%LOG%"
                robocopy "%SRC%" "%DST%" /E /IS /R:10 /W:1 /NFL /NDL /NJH >> "%LOG%" 2>&1
                if errorlevel 8 (
                    echo [%date% %time%] FAILED >> "%LOG%"
                ) else (
                    echo [%date% %time%] OK, restarting >> "%LOG%"
                    rmdir /s /q "%SRC%" >> "%LOG%" 2>&1
                )
                start "" "%EXE%"
                """, ct);

            // 3. Запускаем скрипт отдельным процессом и завершаем приложение
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{batPath}\" {Environment.ProcessId} \"{staging}\" \"{appDir}\" \"{exePath}\" \"{logPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            progress?.Report("Перезапуск для установки обновления…");
            await Task.Delay(300, ct); // дать cmd гарантированно стартовать
            System.Windows.Application.Current.Shutdown();
        }
    }
}
