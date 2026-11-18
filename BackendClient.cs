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
    /// Клиент Python-бэкенда (FastAPI), оборачивающего репозиторий ManagerDataBase.
    /// Бэкенд запускается отдельно: см. backend/README.md
    /// </summary>
    public class BackendClient : IDisposable
    {
        private static readonly HttpClient Http = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:8000/"), // при необходимости замените на адрес сервера
            Timeout = TimeSpan.FromMinutes(30)
        };

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

        /// <summary>Проверить доступность бэкенда.</summary>
        public async Task<bool> IsAliveAsync(CancellationToken ct = default)
        {
            try
            {
                var resp = await Http.GetAsync("health", ct);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Запустить обработку ДСЕ на бэкенде.
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

            using var resp = await Http.PostAsJsonAsync("run", request, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<RunResponse>(body, options) ?? new RunResponse();

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(result.Detail ?? $"Бэкенд вернул {(int)resp.StatusCode}");

            return result;
        }

        public void Dispose() { }
    }
}
