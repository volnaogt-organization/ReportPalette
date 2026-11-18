using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HotkeyApp
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint VK_SPACE = 0x20;
        private const int HOTKEY_ID = 9000;

        private HwndSource _source;
        private DateTime _lastActivateTime = DateTime.MinValue;

        public MainWindow()
        {
            InitializeComponent();

            // Принудительно создаём HWND, даже если окно скрыто —
            // иначе OnSourceInitialized не вызовется и хоткей не зарегистрируется
            IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
            _source = HwndSource.FromHwnd(handle);
            _source.AddHook(HwndHook);

            // Win + Space (можно заменить на MOD_CONTROL | MOD_SHIFT)
            bool ok = RegisterHotKey(handle, HOTKEY_ID, MOD_WIN, VK_SPACE);
            if (!ok)
            {
                // Пробуем запасную комбинацию
                ok = RegisterHotKey(handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, VK_SPACE);
                if (!ok)
                    MessageBox.Show("Не удалось зарегистрировать горячую клавишу — возможно, комбинация занята другой программой.",
                                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // Скрываем окно после создания handle
            this.Visibility = Visibility.Hidden;
            this.ShowInTaskbar = false;
        }

        protected override void OnClosed(EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HOTKEY_ID);
            _source?.RemoveHook(HwndHook);
            base.OnClosed(e);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                const int WM_HOTKEY = 0x0312;

                if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
                {
                    OnHotkeyPressed();
                    handled = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Ошибка в обработчике хоткея");
            }

            return IntPtr.Zero;
        }

        private void OnHotkeyPressed()
        {
            _lastActivateTime = DateTime.Now;

            this.ShowInTaskbar = true;
            this.Visibility = Visibility.Visible;
            this.WindowState = WindowState.Normal;

            // Гарантированный вывод на передний план
            this.Topmost = true;
            this.Activate();
            this.Topmost = false;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                MyTextBox.Focus();
                MyTextBox.SelectAll();
            }));
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Игнорируем ложное событие сразу после активации
            if ((DateTime.Now - _lastActivateTime).TotalMilliseconds < 500)
                return;

            this.Visibility = Visibility.Hidden;
            this.ShowInTaskbar = false;
        }
    }
}