using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace HotkeyApp
{
    /// <summary>
    /// Значок приложения в системном трее (область скрытых значков Windows).
    /// Даёт доступ к палитре, даже если глобальный хоткей не зарегистрировался
    /// или занят другой программой:
    ///   двойной клик — открыть палитру;
    ///   правый клик — меню: Открыть палитру / Настройки / Выход.
    /// </summary>
    public sealed class TrayIconService : IDisposable
    {
        private NotifyIcon? _icon;

        /// <summary>Устанавливает значок в трей. Действия вызываются в UI-потоке.</summary>
        public void Install(Action showPalette, Action openSettings, Action exitApp)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть палитру", null, (_, _) => showPalette());
            menu.Items.Add("Настройки", null, (_, _) => openSettings());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, (_, _) => exitApp());

            _icon = new NotifyIcon
            {
                Text = "ReportPalette",
                Icon = LoadIcon(),
                Visible = true,
                ContextMenuStrip = menu,
            };
            _icon.DoubleClick += (_, _) => showPalette();
        }

        /// <summary>Всплывающее уведомление от значка в трее.</summary>
        public void ShowBalloon(string title, string text)
        {
            try { _icon?.ShowBalloonTip(6000, title, text, ToolTipIcon.Warning); }
            catch { /* уведомления могут быть отключены в Windows */ }
        }

        /// <summary>Иконка exe приложения (app.ico из csproj), запасной вариант — системная.</summary>
        private static Icon LoadIcon()
        {
            try
            {
                var path = Process.GetCurrentProcess().MainModule?.FileName;
                if (path != null)
                {
                    var ico = Icon.ExtractAssociatedIcon(path);
                    if (ico != null) return ico;
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        public void Dispose()
        {
            if (_icon == null) return;
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
    }
}
