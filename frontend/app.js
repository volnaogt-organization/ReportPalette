/* ReportPalette — веб-палитра.
 * Хранение настроек: localStorage. Хеш-строка RP2 совместима с WPF-версией.
 * Бэкенд доступен как /api (nginx проксирует на FastAPI-контейнер).
 */
(function () {
  'use strict';

  var LS_KEY = 'reportpalette.web.v1';

  var BUILTIN = [
    { title: 'Отчёт 1010: введите ДСЕ через $', text: '' },
    { title: 'Отчёт 2012: введите ДСЕ через $', text: '' },
    { title: 'Настройки', text: '@settings' }
  ];

  // Секции RP2 (зеркало SettingsSections из C#)
  var SECTION_NAMES = {
    hotkey: 'Хоткей', window: 'Режим окна', launch: 'Панель запуска',
    plugins: 'Плагины', tasks: 'Задачи расписания', backend: 'Адрес бэкенда',
    general: 'Общие настройки'
  };
  var WEB_SECTIONS = ['launch', 'general']; // из браузера переносим только их

  // ---------- настройки ----------

  function defaultSettings() {
    return { defaultProgram: 1010, launchItems: [] };
  }

  function loadSettings() {
    try {
      var raw = localStorage.getItem(LS_KEY);
      if (raw) {
        var s = JSON.parse(raw);
        if (s && typeof s === 'object') {
          return {
            defaultProgram: s.defaultProgram === 2012 ? 2012 : 1010,
            launchItems: Array.isArray(s.launchItems) ? s.launchItems : []
          };
        }
      }
    } catch (e) { /* повреждённое хранилище — дефолты */ }
    return defaultSettings();
  }

  function saveSettings() {
    localStorage.setItem(LS_KEY, JSON.stringify(settings));
  }

  var settings = loadSettings();

  // ---------- RP2: base64 + sha256-8 ----------

  function b64encodeUtf8(str) {
    var bytes = new TextEncoder().encode(str);
    var bin = '';
    for (var i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
    return btoa(bin);
  }

  function b64decodeUtf8(b64) {
    var bin = atob(b64);
    var bytes = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return new TextDecoder().decode(bytes);
  }

  function sha256short(ascii) { return sha256(ascii).slice(0, 16); }

  function exportHash(sections) {
    var payload = {};
    if (sections.indexOf('launch') >= 0) payload.launchItems = settings.launchItems;
    if (sections.indexOf('general') >= 0) payload.defaultProgram = settings.defaultProgram;
    var b64 = b64encodeUtf8(JSON.stringify(payload));
    return 'RP2.' + sections.join(',') + '.' + b64 + '.' + sha256short(b64);
  }

  // Разбор RP2 и v1. Возвращает { isV1, sections, payload, counts } или бросает Error.
  function parseHash(str) {
    str = (str || '').trim();
    if (!str) throw new Error('Пустая строка.');

    var payloadB64, sections, isV1;
    if (str.indexOf('RP2.') === 0) {
      var parts = str.split('.');
      if (parts.length !== 4) throw new Error('Неверный формат (ожидалось: RP2.секции.Base64.Хеш).');
      sections = parts[1].split(',').filter(function (s) { return SECTION_NAMES[s]; });
      if (!sections.length) throw new Error('В строке нет ни одной известной секции.');
      payloadB64 = parts[2];
      if (sha256short(payloadB64) !== parts[3]) throw new Error('Контрольная хеш-сумма не совпала — строка повреждена.');
      isV1 = false;
    } else {
      var dot = str.lastIndexOf('.');
      if (dot <= 0 || dot === str.length - 1) throw new Error('Неверный формат строки.');
      payloadB64 = str.slice(0, dot);
      if (sha256short(payloadB64) !== str.slice(dot + 1)) throw new Error('Контрольная хеш-сумма не совпала — строка повреждена.');
      sections = Object.keys(SECTION_NAMES); // v1 = все секции
      isV1 = true;
    }

    var payload;
    try { payload = JSON.parse(b64decodeUtf8(payloadB64)); }
    catch (e) { throw new Error('Не удалось прочитать настройки из строки: ' + e.message); }

    return {
      isV1: isV1,
      sections: sections,
      payload: payload,
      launchCount: Array.isArray(payload.launchItems) ? payload.launchItems.length : 0
    };
  }

  function applyImport(parsed, mergeLists) {
    var p = parsed.payload;
    if (Array.isArray(p.launchItems)) {
      if (mergeLists) {
        var map = {};
        settings.launchItems.forEach(function (i) { map[i.name.toLowerCase()] = i; });
        p.launchItems.forEach(function (i) {
          if (i && i.name) map[String(i.name).toLowerCase()] = { name: String(i.name), path: String(i.path || ''), arguments: String(i.arguments || '') };
        });
        settings.launchItems = Object.keys(map).map(function (k) { return map[k]; });
      } else {
        settings.launchItems = p.launchItems
          .filter(function (i) { return i && i.name; })
          .map(function (i) { return { name: String(i.name), path: String(i.path || ''), arguments: String(i.arguments || '') }; });
      }
    }
    if (p.defaultProgram === 1010 || p.defaultProgram === 2012) settings.defaultProgram = p.defaultProgram;
    saveSettings();
    renderLaunchList();
    defaultProgramSelect.value = String(settings.defaultProgram);
  }

  // ---------- палитра ----------

  var input = document.getElementById('queryInput');
  var dropdown = document.getElementById('dropdown');
  var toastEl = document.getElementById('toast');
  var toastTimer = null;

  function toast(msg, isError) {
    toastEl.textContent = msg;
    toastEl.className = 'toast' + (isError ? ' error' : '');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toastEl.classList.add('hidden'); }, 6000);
  }

  function currentItems() {
    var q = input.value.trim();
    if (!q) return [];
    var ql = q.toLowerCase();
    var items = BUILTIN
      .filter(function (c) { return c.title.toLowerCase().indexOf(ql) >= 0; })
      .map(function (c) { return { label: c.title, run: function () { executeInput(c.text || c.title); } }; });
    settings.launchItems.forEach(function (i) {
      if (i.name.toLowerCase().indexOf(ql) >= 0) {
        items.push({ label: '▶ ' + i.name, run: function () { openLaunch(i); } });
      }
    });
    return items;
  }

  function renderDropdown() {
    var items = currentItems();
    if (!items.length) { dropdown.classList.add('hidden'); return; }
    dropdown.innerHTML = '';
    items.forEach(function (item, idx) {
      var li = document.createElement('li');
      li.textContent = item.label;
      if (idx === 0) li.classList.add('selected');
      li.addEventListener('click', function () { hideDropdown(); item.run(); });
      dropdown.appendChild(li);
    });
    dropdown.classList.remove('hidden');
  }

  function hideDropdown() { dropdown.classList.add('hidden'); }

  function moveSelection(delta) {
    var lis = dropdown.querySelectorAll('li');
    if (!lis.length) return;
    var idx = Array.prototype.findIndex.call(lis, function (li) { return li.classList.contains('selected'); });
    lis[idx].classList.remove('selected');
    idx = Math.min(Math.max(idx + delta, 0), lis.length - 1);
    lis[idx].classList.add('selected');
    lis[idx].scrollIntoView({ block: 'nearest' });
  }

  input.addEventListener('input', renderDropdown);
  input.addEventListener('keydown', function (e) {
    if (!dropdown.classList.contains('hidden')) {
      if (e.key === 'Escape') { hideDropdown(); e.preventDefault(); return; }
      if (e.key === 'ArrowDown') { moveSelection(1); e.preventDefault(); return; }
      if (e.key === 'ArrowUp') { moveSelection(-1); e.preventDefault(); return; }
    }
    if (e.key === 'Enter') { hideDropdown(); executeInput(input.value); e.preventDefault(); }
  });
  document.addEventListener('keydown', function (e) {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
      e.preventDefault(); input.focus(); input.select();
    }
  });

  function openLaunch(item) {
    if (!item.path) { toast('У «' + item.name + '» не задан URL', true); return; }
    window.open(item.path + (item.arguments || ''), '_blank', 'noopener');
  }

  function executeInput(raw) {
    var text = (raw || '').trim();
    if (!text) return;

    if (text === '@settings' || text.toLowerCase() === 'настройки') { openSettings(); return; }

    var app = settings.launchItems.find(function (i) {
      return i.name.toLowerCase() === text.replace(/^[▶\s]+/, '').toLowerCase();
    });
    if (app) { openLaunch(app); return; }

    var program = settings.defaultProgram;
    if (text.indexOf('2012') >= 0) program = 2012;
    if (text.indexOf('1010') >= 0) program = 1010;

    var dseList = text.split(/[$;]/).map(function (s) { return s.trim(); }).filter(Boolean);

    var isBuiltin = BUILTIN.some(function (c) { return c.title.toLowerCase() === text.toLowerCase(); });
    if (isBuiltin || !dseList.length) return;

    runReport(program, dseList);
  }

  function runReport(program, dseList) {
    toast('Отправлено на бэкенд: отчёт ' + program + ', ДСЕ: ' + dseList.length + '…');
    fetch('/api/run', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ program: program, dse_list: dseList, save_input: 0, max_threads: 6 })
    })
      .then(function (r) {
        return r.json().then(function (body) { return { ok: r.ok, body: body }; });
      })
      .then(function (res) {
        if (res.ok) {
          toast('Готово: обработано ' + res.body.processed + ' ДСЕ (отчёт ' + res.body.program + ').');
          input.value = '';
        } else {
          toast('Ошибка бэкенда: ' + (res.body.detail || res.status), true);
        }
      })
      .catch(function (e) {
        toast('Бэкенд недоступен: ' + e.message, true);
      });
  }

  // ---------- статус бэкенда ----------

  var healthDot = document.getElementById('healthDot');
  var healthText = document.getElementById('healthText');

  function checkHealth() {
    fetch('/api/health')
      .then(function (r) { return r.json(); })
      .then(function (h) {
        if (h.core_imported) {
          healthDot.className = 'dot dot-ok';
          healthText.textContent = 'бэкенд онлайн';
        } else {
          healthDot.className = 'dot dot-bad';
          healthText.textContent = 'бэкенд без ядра ManagerDataBase';
        }
      })
      .catch(function () {
        healthDot.className = 'dot dot-bad';
        healthText.textContent = 'бэкенд недоступен';
      });
  }
  checkHealth();
  setInterval(checkHealth, 30000);

  // ---------- настройки ----------

  var modal = document.getElementById('settingsModal');
  var launchList = document.getElementById('launchList');
  var defaultProgramSelect = document.getElementById('defaultProgram');

  function openSettings() {
    renderLaunchList();
    defaultProgramSelect.value = String(settings.defaultProgram);
    modal.classList.remove('hidden');
  }
  function closeSettings() { modal.classList.add('hidden'); }

  document.getElementById('settingsBtn').addEventListener('click', openSettings);
  document.getElementById('closeSettingsBtn').addEventListener('click', closeSettings);
  modal.addEventListener('click', function (e) { if (e.target === modal) closeSettings(); });

  function renderLaunchList() {
    launchList.innerHTML = '';
    settings.launchItems.forEach(function (item, idx) {
      var li = document.createElement('li');
      var link = document.createElement('a');
      link.textContent = item.name;
      link.href = item.path || '#';
      link.target = '_blank';
      link.rel = 'noopener';
      var del = document.createElement('button');
      del.textContent = '✕';
      del.addEventListener('click', function () {
        settings.launchItems.splice(idx, 1);
        saveSettings(); renderLaunchList();
      });
      li.appendChild(link);
      li.appendChild(del);
      launchList.appendChild(li);
    });
  }

  document.getElementById('addLaunchBtn').addEventListener('click', function () {
    var nameEl = document.getElementById('launchName');
    var urlEl = document.getElementById('launchUrl');
    var name = nameEl.value.trim(), url = urlEl.value.trim();
    if (!name || !url) { toast('Заполните название и URL', true); return; }
    settings.launchItems.push({ name: name, path: url, arguments: '' });
    nameEl.value = ''; urlEl.value = '';
    saveSettings(); renderLaunchList();
  });

  defaultProgramSelect.addEventListener('change', function () {
    settings.defaultProgram = Number(defaultProgramSelect.value);
    saveSettings();
  });

  // ---------- перенос настроек ----------

  var exportHashEl = document.getElementById('exportHash');
  var importPreviewEl = document.getElementById('importPreview');

  document.getElementById('genHashBtn').addEventListener('click', function () {
    var sections = [];
    if (document.getElementById('secLaunch').checked) sections.push('launch');
    if (document.getElementById('secGeneral').checked) sections.push('general');
    if (!sections.length) { toast('Отметьте хотя бы одну секцию', true); return; }
    exportHashEl.value = exportHash(sections);
  });

  document.getElementById('copyHashBtn').addEventListener('click', function () {
    if (!exportHashEl.value) document.getElementById('genHashBtn').click();
    navigator.clipboard.writeText(exportHashEl.value).then(
      function () { toast('Скопировано в буфер обмена'); },
      function () { exportHashEl.select(); document.execCommand('copy'); toast('Скопировано'); });
  });

  document.getElementById('importHashBtn').addEventListener('click', function () {
    var raw = document.getElementById('importHash').value;
    var parsed;
    try { parsed = parseHash(raw); }
    catch (e) {
      importPreviewEl.classList.remove('hidden');
      importPreviewEl.textContent = 'Ошибка: ' + e.message;
      return;
    }
    var merge = document.querySelector('input[name="listMode"]:checked').value === 'merge';
    var lines = [];
    if (parsed.isV1) lines.push('Строка старого формата (v1) — все настройки.');
    lines.push('Секции: ' + parsed.sections.map(function (s) { return SECTION_NAMES[s]; }).join(', '));
    if (parsed.launchCount) lines.push('Приложений в строке: ' + parsed.launchCount);
    var skipped = parsed.sections.filter(function (s) { return WEB_SECTIONS.indexOf(s) < 0; });
    if (skipped.length) {
      lines.push('Пропущены десктопные секции: ' + skipped.map(function (s) { return SECTION_NAMES[s]; }).join(', '));
    }
    applyImport(parsed, merge);
    importPreviewEl.classList.remove('hidden');
    importPreviewEl.textContent = lines.join('\n') + '\n\nНастройки применены (' + (merge ? 'объединение' : 'замена') + ').';
    toast('Настройки применены');
  });

  input.focus();
})();
