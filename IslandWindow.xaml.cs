using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HotkeyApp
{
    /// <summary>
    /// «Остров» — таблетка сверху экрана (идея в духе Dynamic Island):
    /// всегда на виду, принимает drag-and-drop файлов с рабочего стола
    /// и из Проводника. Excel с ДСЕ — выбор отчёта и столбца прямо из таблетки,
    /// прочие файлы — команды модулей с вводом «путь к файлу», папка —
    /// выдача доступа к директории. Прогресс и кнопка результата — в самой
    /// таблетке; через несколько секунд простоя она сворачивается обратно.
    /// Клик по таблетке — кнопки палитры и настроек; перетаскивание мышью
    /// двигает остров, позиция запоминается.
    /// </summary>
    public partial class IslandWindow : Window
    {
        private readonly MainWindow _palette;
        private AppSettings _settings = SettingsService.Load();

        private List<ModuleCommand> _commands = new();
        private readonly Dictionary<string, BackendClient> _clients = new();

        /// <summary>Excel-файл, который модуль записал в свой рабочий каталог.</summary>
        private string? _lastResultFile;

        private readonly DispatcherTimer _collapseTimer;

        // Перетаскивание самой таблетки: клик отличаем от драга по порогу сдвига
        private Point _pressPoint;
        private bool _dragMoved;
        private bool _userPositioned; // есть сохранённая позиция — не центрируем

        public IslandWindow(MainWindow palette)
        {
            _palette = palette;
            InitializeComponent();
            RefreshCommands();

            _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); Collapse(); };

            Loaded += (_, _) => RestorePosition();
            SizeChanged += (_, _) => { if (!_userPositioned) CenterTop(); };
        }

        /// <summary>Перечитывает команды из манифестов модулей (после установки/обновления).</summary>
        public void RefreshCommands() => _commands = ModuleCatalog.LoadCommands();

        /// <summary>Перечитывает конфиг с диска перед записью — остров хранит
        /// свою копию настроек, иначе перезапишет свежие изменения палитры.</summary>
        private void SaveIslandSettings(Action<AppSettings> change)
        {
            _settings = SettingsService.Load();
            change(_settings);
            try { SettingsService.Save(_settings); } catch { }
        }

        // ---------------- Позиция и перемещение ----------------

        private void RestorePosition()
        {
            if (_settings.IslandLeft.HasValue && _settings.IslandTop.HasValue)
            {
                _userPositioned = true;
                Left = _settings.IslandLeft.Value;
                Top = _settings.IslandTop.Value;
            }
            else
            {
                CenterTop();
            }
        }

        private void CenterTop()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 8;
        }

        private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pressPoint = e.GetPosition(this);
            _dragMoved = false;
            Pill.CaptureMouse();
        }

        private void Pill_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragMoved || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _pressPoint.X) < 6 && Math.Abs(pos.Y - _pressPoint.Y) < 6) return;

            _dragMoved = true;
            Pill.ReleaseMouseCapture();
            try { DragMove(); } catch { }
        }

        private void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            Pill.ReleaseMouseCapture();
            if (_dragMoved)
            {
                // запоминаем, куда пользователь перетащил остров
                _userPositioned = true;
                SaveIslandSettings(s => { s.IslandLeft = Left; s.IslandTop = Top; });
            }
            else
            {
                ToggleExpand();
            }
        }

        // ---------------- Свёрнуто / развёрнуто ----------------

        private void Expand(string status)
        {
            StatusText.Text = status;
            ExpandedPanel.Visibility = Visibility.Visible;
        }

        private void Collapse()
        {
            ExpandedPanel.Visibility = Visibility.Collapsed;
            ActionPanel.Children.Clear();
        }

        private void ScheduleCollapse()
        {
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }

        private void ToggleExpand()
        {
            if (ExpandedPanel.Visibility == Visibility.Visible)
            {
                Collapse();
                return;
            }
            ActionPanel.Children.Clear();
            OpenResultButton.Visibility = _lastResultFile != null ? Visibility.Visible : Visibility.Collapsed;
            Expand("Перетащите на остров Excel-файл с ДСЕ — выберите отчёт.\n" +
                   "Перенос папки выдаёт доступ к директории.");
            ScheduleCollapse();
        }

        // ---------------- Drag-and-drop файлов ----------------

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;

            if (files)
            {
                Pill.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                if (ExpandedPanel.Visibility != Visibility.Visible)
                    Expand("Отпустите файл — выберите действие");
                _collapseTimer.Stop(); // не сворачиваться посреди переноса
            }
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            // Событие всплывает и при выходе за пределы дочерних элементов —
            // сворачиваемся только когда курсор реально за границами окна.
            var pt = e.GetPosition(this);
            if (pt.X < 0 || pt.Y < 0 || pt.X > ActualWidth || pt.Y > ActualHeight)
            {
                Pill.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0xFF, 0xFF));
                Collapse();
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            Pill.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0xFF, 0xFF));
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths ||
                paths.Length == 0) return;

            // папка — выдача доступа к директории (как на палитре)
            if (paths.Length == 1 && Directory.Exists(paths[0]))
            {
                GrantDirectoryAccess(paths[0]);
                return;
            }

            var file = paths[0];
            if (!File.Exists(file)) return;

            var ext = Path.GetExtension(file).ToLowerInvariant();
            bool isExcel = ext is ".xlsx" or ".xls" or ".xlsm" or ".xlsb";

            var matches = isExcel
                ? _commands.Where(c => c.Input == "excel_column").ToList()
                : _commands.Where(c => c.IsFileInput).ToList();

            if (matches.Count == 0)
            {
                Expand($"Для файла {Path.GetFileName(file)} нет подходящей команды.\n" +
                       (isExcel
                           ? "Ни один модуль не объявил команду «столбец из Excel»."
                           : "Ни один модуль не объявил команду с вводом «путь к файлу»."));
                ScheduleCollapse();
                return;
            }

            if (matches.Count == 1)
            {
                _ = RunWithFileAsync(matches[0], file);
                return;
            }

            // Несколько команд подходят — показываем выбор кнопками в таблетке
            ActionPanel.Children.Clear();
            foreach (var cmd in matches)
            {
                var btn = new Button
                {
                    Content = cmd.Title,
                    FontSize = 12,
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(0, 0, 0, 4),
                    HorizontalContentAlignment = HorizontalAlignment.Left
                };
                btn.Click += (_, _) =>
                {
                    ActionPanel.Children.Clear();
                    _ = RunWithFileAsync(cmd, file);
                };
                ActionPanel.Children.Add(btn);
            }
            Expand($"Что сделать с файлом {Path.GetFileName(file)}?");
        }

        /// <summary>Перенос папки: подтверждение и сохранение доступа к директории.</summary>
        private void GrantDirectoryAccess(string dir)
        {
            var result = MessageBox.Show(this,
                $"Выдать доступ приложению к директории?\n\n{dir}",
                "Разрешить доступ", MessageBoxButton.OKCancel,
                MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (result != MessageBoxResult.OK)
            {
                Collapse();
                return;
            }

            SaveIslandSettings(s => s.AllowedDirectory = dir);
            Expand($"Доступ к директории выдан:\n{dir}");
            ScheduleCollapse();
        }

        // ---------------- Выполнение команд ----------------

        private BackendClient ClientFor(string moduleName)
        {
            if (!_clients.TryGetValue(moduleName, out var client))
                _clients[moduleName] = client = new BackendClient(ModuleManager.GetModuleBaseAddress(moduleName));
            return client;
        }

        /// <summary>Запуск команды с перенесённым файлом: excel_column — сначала
        /// выбор листа/столбца и чтение ДСЕ; file — сразу выполнение.</summary>
        private async System.Threading.Tasks.Task RunWithFileAsync(ModuleCommand cmd, string file)
        {
            ActionPanel.Children.Clear();
            OpenResultButton.Visibility = Visibility.Collapsed;

            if (cmd.Input == "excel_column")
            {
                Expand($"[{cmd.Title}] читаю листы {Path.GetFileName(file)}…");
                (string Sheet, int ColumnIndex)? pick;
                try
                {
                    pick = await ExcelColumnDialog.PickAsync(this, ClientFor(cmd.ModuleName), file, cmd.Title);
                }
                catch (Exception ex)
                {
                    Expand($"[{cmd.Title}] не удалось прочитать файл:\n{ex.Message}");
                    ScheduleCollapse();
                    return;
                }
                if (pick == null)
                {
                    Expand($"[{cmd.Title}] выбор столбца отменён");
                    ScheduleCollapse();
                    return;
                }

                Expand($"[{cmd.Title}] читаю столбец «{pick.Value.Sheet}»…");
                try
                {
                    var json = await ClientFor(cmd.ModuleName).PostAsync(
                        "/excel/dse_column",
                        new Dictionary<string, object?>
                        {
                            ["path"] = file,
                            ["sheet"] = pick.Value.Sheet,
                            ["column_index"] = pick.Value.ColumnIndex
                        });

                    var dse = json.GetProperty("dse_list").EnumerateArray()
                                  .Select(el => el.GetString() ?? "")
                                  .Where(s => s.Length > 0)
                                  .ToList();
                    if (dse.Count == 0)
                    {
                        Expand($"[{cmd.Title}] в столбце нет значений");
                        ScheduleCollapse();
                        return;
                    }
                    await ExecuteAsync(cmd, dse, null);
                }
                catch (Exception ex)
                {
                    Expand($"[{cmd.Title}] ошибка чтения столбца:\n{ex.Message}");
                    ScheduleCollapse();
                }
            }
            else // file
            {
                await ExecuteAsync(cmd, null, file);
            }
        }

        private async System.Threading.Tasks.Task ExecuteAsync(
            ModuleCommand cmd, IReadOnlyList<string>? dseList, string? filePath)
        {
            Expand($"⏳ [{cmd.Title}] выполняется…");
            _collapseTimer.Stop();
            try
            {
                var json = await ClientFor(cmd.ModuleName)
                    .PostAsync(cmd.Endpoint, cmd.BuildPayload(dseList, filePath));
                CommandHistory.Register(cmd.Key);

                UpdateResultFile(cmd);
                Expand($"✓ [{cmd.Title}] {DescribeResult(json)}");
            }
            catch (Exception ex)
            {
                Expand($"✗ [{cmd.Title}] ошибка:\n{ex.Message}");
            }
            ScheduleCollapse();
        }

        /// <summary>Кнопка «Открыть результат» — если модуль записал Excel-отчёт.</summary>
        private void UpdateResultFile(ModuleCommand cmd)
        {
            var dir = ModuleManager.GetInstalledAppDir(cmd.ModuleName);
            var fileName = string.IsNullOrEmpty(cmd.ResultFile)
                ? "result_manager_database_reports.xlsx"
                : cmd.ResultFile;
            var file = dir == null ? null : Path.Combine(dir, fileName);
            _lastResultFile = file != null && File.Exists(file) ? file : null;
            OpenResultButton.ToolTip = _lastResultFile ?? "";
            OpenResultButton.Visibility = _lastResultFile != null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OpenResultButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastResultFile == null) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _lastResultFile,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = "Не удалось открыть файл: " + ex.Message;
            }
        }

        private static string DescribeResult(JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Object)
            {
                if (json.TryGetProperty("processed", out var pr) &&
                    json.TryGetProperty("program", out var pg))
                    return $"готово, обработано ДСЕ: {pr.GetInt32()} (отчёт {pg.GetInt32()})";
                if (json.TryGetProperty("status", out var st))
                    return "статус: " + (st.GetString() ?? "ok");
            }
            return "готово";
        }

        // ---------------- Кнопки палитры и настроек ----------------

        private void PaletteButton_Click(object sender, RoutedEventArgs e)
        {
            Collapse();
            _palette.ShowPalette();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Collapse();
            _palette.OpenSettings();
        }
    }
}
