using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HotkeyApp
{
    /// <summary>
    /// Клиент локального сервиса модуля (FastAPI, 127.0.0.1).
    /// Адрес модуля выдаёт ModuleManager — он же скачивает и запускает модуль.
    /// </summary>
    public class BackendClient : IDisposable
    {
        private readonly HttpClient _http;

        public BackendClient(string baseAddress)
        {
            _http = new HttpClient
            {
                BaseAddress = new Uri(baseAddress),
                Timeout = TimeSpan.FromMinutes(30)
            };
        }

        public class RunRequest
        {
            [JsonPropertyName("program")] public int Program { get; set; }
            [JsonPropertyName("dse_list")] public List<string> DseList { get; set; } = new();
            [JsonPropertyName("save_input")] public int SaveInput { get; set; }
            [JsonPropertyName("max_threads")] public int MaxThreads { get; set; } = 6;
        }

        public class RunResponse
        {
            [JsonPropertyName("status")] public string? Status { get; set; }
            [JsonPropertyName("program")] public int Program { get; set; }
            [JsonPropertyName("processed")] public int Processed { get; set; }
            [JsonPropertyName("detail")] public string? Detail { get; set; }
        }

        /// <summary>Проверить доступность сервиса модуля.</summary>
        public async Task<bool> IsAliveAsync(CancellationToken ct = default)
        {
            try
            {
                var resp = await _http.GetAsync("health", ct);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Запустить обработку ДСЕ модулем.
        /// program: 1010 или 2012; dse — список шифров ДСЕ.
        /// </summary>
        public async Task<RunResponse> RunAsync(int program, IEnumerable<string> dse,
                                                bool saveToExcel = false,
                                                CancellationToken ct = default)
        {
            var request = new RunRequest
            {
                Program = program,
                DseList = new List<string>(dse),
                SaveInput = saveToExcel ? 1 : 0
            };

            using var resp = await _http.PostAsJsonAsync("run", request, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<RunResponse>(body, options) ?? new RunResponse();

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(result.Detail ?? $"Модуль вернул {(int)resp.StatusCode}");

            return result;
        }

        public void Dispose() => _http.Dispose();
    }
}
