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
    /// и из Проводника. Локальный файл копируется в папку острова (задаётся
    /// в ⚙ настройках острова), сетевой (UNC/сетевой диск) — только после
    /// вопроса: копия или ярлык. Сохранённые файлы лежат на «полке» в раскрытой
    /// таблетке — открыть или удалить в один клик. Дальше по типу файла:
    /// Excel с ДСЕ — выбор отчёта и столбца, прочие — команды модулей с вводом
    /// «путь к файлу», папка — выдача доступа к директории. Прогресс и кнопка
    /// результата — в самой таблетке; через несколько секунд простоя она
    /// сворачивается обратно. Перетаскивание мышью двигает остров,
    /// позиция запоминается.
    /// </summary>
    public partial class IslandWindow : Window
    {
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

        public IslandWindow()
        {
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
            ShelfList.Children.Clear();
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
                   "Файл сначала копируется в папку из ⚙ настроек острова.");
            RefreshShelf();
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

            var dropped = paths[0];
            if (!File.Exists(dropped)) return;

            // Сетевые файлы молча не копируем: спрашиваем — копия или ярлык
            if (IsNetworkPath(dropped))
            {
                var choice = AskNetworkChoice(dropped);
                if (choice == null) { Collapse(); return; }
                if (choice == false)
                {
                    CreateInboxShortcut(dropped);
                    return;
                }
                // true — копируем и дальше обычным путём
            }

            // В первую очередь сохраняем файл в папку пользователя
            // (задаётся в настройках острова, кнопка ⚙ в раскрытой таблетке)
            string file;
            try { file = CopyToInbox(dropped); }
            catch (Exception ex)
            {
                Expand($"Не удалось сохранить файл:\n{ex.Message}");
                ScheduleCollapse();
                return;
            }
            RefreshShelf();

            var ext = Path.GetExtension(file).ToLowerInvariant();
            bool isExcel = ext is ".xlsx" or ".xls" or ".xlsm" or ".xlsb";

            var matches = isExcel
                ? _commands.Where(c => c.Input == "excel_column").ToList()
                : _commands.Where(c => c.IsFileInput).ToList();

            if (matches.Count == 0)
            {
                Expand($"Файл сохранён:\n{file}\n\nДля него нет подходящей команды.\n" +
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
            Expand($"Файл сохранён:\n{file}\n\nЧто сделать с ним?");
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

        // ---------------- Папка для перенесённых файлов ----------------

        /// <summary>Папка, куда копируются перенесённые на остров файлы
        /// (читаем свежую с диска — могли изменить в диалоге настроек острова).</summary>
        private string CurrentInboxDir()
        {
            var dir = SettingsService.Load().IslandInboxDir;
            return string.IsNullOrWhiteSpace(dir)
                ? Path.Combine(ModuleManager.RootDir, "inbox")
                : dir;
        }

        /// <summary>Копирует перенесённый файл в папку пользователя; при совпадении
        /// имени добавляет суффикс (1), (2)… Файл уже в этой папке — берётся как есть.</summary>
        private string CopyToInbox(string file)
        {
            var dir = CurrentInboxDir();
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)),
                              Path.GetFullPath(dir.TrimEnd('\\', '/')),
                              StringComparison.OrdinalIgnoreCase))
                return file;

            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, Path.GetFileName(file));
            if (File.Exists(dest))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var ext = Path.GetExtension(file);
                int i = 1;
                do { dest = Path.Combine(dir, $"{name} ({i++}){ext}"); }
                while (File.Exists(dest));
            }
            File.Copy(file, dest);
            return dest;
        }

        // ---------------- Сетевые файлы: копия или ярлык ----------------

        /// <summary>Сетевой путь: UNC (\\server\share) или подключённый сетевой диск.</summary>
        private static bool IsNetworkPath(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (full.StartsWith(@"\\", StringComparison.Ordinal)) return true;
                var root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return false;
                return new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch { return false; } // не смогли определить — считаем локальным
        }

        /// <summary>Сетевой файл: true — копировать, false — ярлык, null — отмена.</summary>
        private bool? AskNetworkChoice(string file)
        {
            bool? result = null;

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock
            {
                Text = $"Файл из сетевой папки:\n{file}\n\n" +
                       "Скопировать его в папку острова или оставить на месте и создать ярлык?",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            });

            var win = new Window
            {
                Title = "Сетевой файл",
                Content = panel,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true
            };

            var copy = new Button
            {
                Content = "Копировать", IsDefault = true,
                MinWidth = 100, Margin = new Thickness(0, 0, 6, 0)
            };
            copy.Click += (_, _) => { result = true; win.DialogResult = true; };
            var link = new Button
            {
                Content = "Создать ярлык",
                MinWidth = 100, Margin = new Thickness(0, 0, 6, 0)
            };
            link.Click += (_, _) => { result = false; win.DialogResult = true; };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            buttons.Children.Add(copy);
            buttons.Children.Add(link);
            buttons.Children.Add(new Button { Content = "Отмена", IsCancel = true, MinWidth = 70 });
            panel.Children.Add(buttons);

            win.ShowDialog();
            return result;
        }

        /// <summary>Ярлык (.lnk) в папке острова на сетевой файл — файл остаётся на месте.</summary>
        private void CreateInboxShortcut(string target)
        {
            try
            {
                var dir = CurrentInboxDir();
                Directory.CreateDirectory(dir);

                var dest = Path.Combine(dir, Path.GetFileName(target) + ".lnk");
                if (File.Exists(dest))
                {
                    var name = Path.GetFileName(target);
                    int i = 1;
                    do { dest = Path.Combine(dir, $"{name} ({i++}).lnk"); }
                    while (File.Exists(dest));
                }

                // WScript.Shell через COM — без дополнительных зависимостей
                var shellType = Type.GetTypeFromProgID("WScript.Shell")
                    ?? throw new InvalidOperationException("WScript.Shell недоступен");
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(dest);
                shortcut.TargetPath = target;
                shortcut.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
                shortcut.Save();

                Expand($"Ярлык создан:\n{dest}\n\nФайл остался в сетевой папке.");
            }
            catch (Exception ex)
            {
                Expand($"Не удалось создать ярлык:\n{ex.Message}");
            }
            RefreshShelf();
            ScheduleCollapse();
        }

        // ---------------- Полка: быстрый доступ к файлам острова ----------------

        /// <summary>Последние 8 файлов из папки острова: клик — открыть, ✕ — удалить.</summary>
        private void RefreshShelf()
        {
            ShelfList.Children.Clear();

            List<FileInfo> files;
            try
            {
                var dir = CurrentInboxDir();
                files = Directory.Exists(dir)
                    ? new DirectoryInfo(dir).GetFiles()
                          .OrderByDescending(f => f.LastWriteTime)
                          .Take(8)
                          .ToList()
                    : new List<FileInfo>();
            }
            catch { files = new List<FileInfo>(); }

            ShelfPanel.Visibility = files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var f in files)
            {
                var path = f.FullName;

                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var open = new Button
                {
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    FontSize = 11,
                    Padding = new Thickness(6, 2, 6, 2),
                    ToolTip = path,
                    Content = new TextBlock
                    {
                        Text = f.Name,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 300
                    }
                };
                open.Click += (_, _) =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        StatusText.Text = "Не удалось открыть файл: " + ex.Message;
                    }
                };

                var del = new Button
                {
                    Content = "✕", FontSize = 10,
                    Padding = new Thickness(4, 0, 4, 0),
                    ToolTip = "Удалить файл из папки острова"
                };
                del.Click += (_, _) =>
                {
                    try { File.Delete(path); } catch { }
                    RefreshShelf();
                };

                Grid.SetColumn(open, 0);
                Grid.SetColumn(del, 1);
                row.Children.Add(open);
                row.Children.Add(del);
                ShelfList.Children.Add(row);
            }
        }

        private void OpenInboxButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = CurrentInboxDir();
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                StatusText.Text = "Не удалось открыть папку: " + ex.Message;
            }
        }

        // ---------------- Настройки острова ----------------

        private void IslandSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            _collapseTimer.Stop(); // не сворачиваться, пока открыт диалог

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock
            {
                Text = "Папка для перенесённых на остров файлов:",
                Margin = new Thickness(0, 0, 0, 5)
            });

            var tb = new TextBox { Text = CurrentInboxDir(), MinWidth = 340 };
            var browse = new Button { Content = "Обзор…", Margin = new Thickness(6, 0, 0, 0) };
            var row = new DockPanel();
            DockPanel.SetDock(browse, Dock.Right);
            row.Children.Add(browse);
            row.Children.Add(tb);
            panel.Children.Add(row);
            panel.Children.Add(new TextBlock
            {
                Text = "Файл, перенесённый на остров, сначала копируется сюда,\n" +
                       "отчёт строится уже по копии.",
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0)
            });

            var win = new Window
            {
                Title = "Настройки острова",
                Content = panel,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true
            };

            browse.Click += (_, _) =>
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    SelectedPath = tb.Text,
                    Description = "Папка для перенесённых файлов"
                };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    tb.Text = dlg.SelectedPath;
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var ok = new Button
            {
                Content = "Сохранить", IsDefault = true,
                MinWidth = 80, Margin = new Thickness(0, 0, 8, 0)
            };
            ok.Click += (_, _) => { win.DialogResult = true; };
            buttons.Children.Add(ok);
            buttons.Children.Add(new Button { Content = "Отмена", IsCancel = true, MinWidth = 80 });
            panel.Children.Add(buttons);

            if (win.ShowDialog() == true)
            {
                SaveIslandSettings(s => s.IslandInboxDir = tb.Text.Trim());
                Expand($"Папка для перенесённых файлов:\n{CurrentInboxDir()}");
            }
            ScheduleCollapse();
        }
    }
}
