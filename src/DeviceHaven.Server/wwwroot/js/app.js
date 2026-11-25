// DeviceHaven Console. Plain JavaScript, no outside libraries or network calls.
// Everything shown comes from this server's admin API. Text from the server is
// always inserted with textContent, never as HTML.
(() => {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const state = { devices: [], settings: null, server: null };

  // ---------- Helpers ----------

  async function api(path, options = {}) {
    const res = await fetch(`/api/admin/${path}`, {
      ...options,
      headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
    });
    if (res.status === 403) {
      showNotice('The console only works when opened on the server itself (https://localhost) until admin sign-in is added.');
      throw new Error('forbidden');
    }
    if (!res.ok && res.status !== 204) throw new Error(`Request failed: ${res.status}`);
    return res.status === 204 ? null : res.json();
  }

  function el(tag, props = {}, ...children) {
    const node = document.createElement(tag);
    for (const [k, v] of Object.entries(props)) {
      if (k === 'class') node.className = v;
      else if (k.startsWith('on')) node.addEventListener(k.slice(2), v);
      else node.setAttribute(k, v);
    }
    for (const child of children) {
      if (child == null) continue;
      node.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }
    return node;
  }

  function showNotice(text) { const n = $('notice'); n.textContent = text; n.hidden = false; }

  function ago(iso) {
    if (!iso) return 'Never';
    const secs = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
    if (secs < 60) return 'Just now';
    if (secs < 3600) return `${Math.floor(secs / 60)} min ago`;
    if (secs < 86400) return `${Math.floor(secs / 3600)} h ago`;
    return new Date(iso).toLocaleDateString();
  }

  function bytes(n) {
    if (!n) return '–';
    const gb = n / 1073741824;
    return gb >= 1 ? `${gb.toFixed(gb >= 100 ? 0 : 1)} GB` : `${Math.round(n / 1048576)} MB`;
  }

  function isOnline(device) {
    if (!device.lastSeen || device.revoked) return false;
    const limit = ((state.settings?.checkInSeconds ?? 300) * 2 + 60) * 1000;
    return Date.now() - new Date(device.lastSeen).getTime() < limit;
  }

  // ---------- Views ----------

  const titles = {
    console: ['Console', 'Every device on your network at a glance'],
    add: ['Add devices', 'Create a code, then install the agent with the MSI'],
    settings: ['Agent settings', 'How often agents check in and report'],
    audit: ['Security & audit', 'Everything that changed, and the server\'s identity'],
  };

  function show(view) {
    if (!titles[view]) view = 'console';
    document.querySelectorAll('.view').forEach((v) => { v.hidden = v.id !== `view-${view}`; });
    document.querySelectorAll('.nav a').forEach((a) => a.classList.toggle('active', a.dataset.view === view));
    $('title').textContent = titles[view][0];
    $('subtitle').textContent = titles[view][1];
    if (view === 'audit') loadAudit();
    if (view === 'settings') fillSettings();
  }

  window.addEventListener('hashchange', () => show(location.hash.slice(1)));

  // ---------- Console ----------

  async function loadDevices() {
    try {
      [state.devices, state.settings, state.server] = await Promise.all([api('devices'), api('settings/agent'), api('server')]);
    } catch { return; }
    $('version').textContent = `v${state.server.version}`;
    renderDevices();
  }

  function renderDevices() {
    const active = state.devices.filter((d) => !d.revoked);
    const online = active.filter(isOnline).length;
    $('s-total').textContent = active.length;
    $('s-online').textContent = online;
    $('s-offline').textContent = active.length - online;
    $('s-online-note').textContent = active.length ? `${Math.round((online / active.length) * 100)}% of devices` : '';
    $('s-offline-note').textContent = active.length - online ? 'Not checked in recently' : '';
    const every = state.settings.checkInSeconds;
    $('s-checkin').textContent = every >= 3600 ? `${Math.round(every / 3600)} h` : every >= 60 ? `${Math.round(every / 60)} min` : `${every} s`;
    $('s-checkin-note').textContent = `±${state.settings.jitterPercent}% spread`;

    const term = $('search').value.trim().toLowerCase();
    const rows = active.filter((d) => !term || `${d.hostname} ${d.osName} ${d.lastIp}`.toLowerCase().includes(term));
    const body = $('devices');
    body.replaceChildren(...rows.map((d) => {
      const on = isOnline(d);
      return el('tr', {},
        el('td', { class: 'name' }, d.hostname),
        el('td', {}, el('span', { class: `status ${on ? 'ok' : 'bad'}` }, on ? 'Online' : 'Offline')),
        el('td', {}, d.osName),
        el('td', {}, d.agentVersion),
        el('td', {}, ago(d.lastSeen)),
        el('td', {}, d.lastIp ?? '–'),
        el('td', {}, el('button', { type: 'button', class: 'btn small', onclick: () => openDevice(d.id) }, 'Details')));
    }));
    $('empty').hidden = active.length > 0;
  }

  $('search').addEventListener('input', renderDevices);

  // ---------- Device details ----------

  async function openDevice(id) {
    const detail = await api(`devices/${id}`);
    const d = detail.device;
    const inv = detail.inventory;
    $('d-name').textContent = d.hostname;
    const fact = (label, value) => el('div', {}, el('span', {}, label), value ?? '–');
    const parts = [
      el('div', { class: 'facts' },
        fact('Status', isOnline(d) ? 'Online' : 'Offline'),
        fact('Last seen', ago(d.lastSeen)),
        fact('Enrolled', new Date(d.enrolledAt).toLocaleString()),
        fact('Agent version', d.agentVersion),
        fact('Operating system', inv ? `${inv.osName} ${inv.osBuild ?? ''}` : d.osName),
        fact('Manufacturer', inv?.manufacturer),
        fact('Model', inv?.model),
        fact('Serial number', inv?.serialNumber),
        fact('Processor', inv ? `${inv.cpuName ?? 'Unknown'} (${inv.cpuCores} threads)` : null),
        fact('Memory', inv ? bytes(inv.totalMemoryBytes) : null),
        fact('Signed-in user', inv?.loggedOnUser),
        fact('Inventory updated', detail.inventoryAt ? ago(detail.inventoryAt) : 'Not yet')),
    ];
    if (inv) {
      parts.push(el('h3', {}, 'Disks'));
      parts.push(el('div', { class: 'table-wrap' }, el('table', {},
        el('thead', {}, el('tr', {}, el('th', {}, 'Drive'), el('th', {}, 'Size'), el('th', {}, 'Free'), el('th', {}, 'BitLocker'))),
        el('tbody', {}, ...inv.disks.map((k) => el('tr', {},
          el('td', {}, k.name), el('td', {}, bytes(k.sizeBytes)), el('td', {}, bytes(k.freeBytes)),
          el('td', {}, k.bitLockerOn == null ? 'Unknown' : k.bitLockerOn ? 'On' : 'Off')))))));
      parts.push(el('h3', {}, `Installed programs (${inv.apps.length})`));
      parts.push(el('div', { class: 'table-wrap' }, el('table', {},
        el('thead', {}, el('tr', {}, el('th', {}, 'Name'), el('th', {}, 'Version'), el('th', {}, 'Publisher'))),
        el('tbody', {}, ...inv.apps.map((a) => el('tr', {},
          el('td', {}, a.name), el('td', {}, a.version ?? '–'), el('td', {}, a.publisher ?? '–')))))));
    }
    parts.push(el('div', {}, el('button', { type: 'button', class: 'btn danger', onclick: () => removeDevice(d) }, 'Remove device')));
    $('d-body').replaceChildren(...parts);
    $('device-dialog').showModal();
  }

  async function removeDevice(d) {
    if (!window.confirm(`Remove ${d.hostname}? It will no longer be able to check in. To add it back, reinstall the agent with a new code.`)) return;
    await api(`devices/${d.id}`, { method: 'DELETE' });
    $('device-dialog').close();
    loadDevices();
  }

  $('d-close').addEventListener('click', () => $('device-dialog').close());

  // ---------- Add devices ----------

  $('code-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const body = {
      maxUses: Number($('max-uses').value),
      validHours: Number($('valid-hours').value),
      note: $('note').value.trim() || null,
    };
    const r = await api('enrollment-codes', { method: 'POST', body: JSON.stringify(body) });
    $('r-code').textContent = r.code;
    $('r-url').textContent = r.serverUrl;
    $('r-fp').textContent = r.fingerprint;
    $('r-uses').textContent = r.maxUses === 1 ? '1 PC' : `Up to ${r.maxUses} PCs`;
    $('r-exp').textContent = new Date(r.expiresAt).toLocaleString();
    $('r-cmd').textContent = r.installCommand;
    $('code-result').hidden = false;
  });

  $('copy-cmd').addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText($('r-cmd').textContent);
      $('copy-cmd').textContent = 'Copied';
      setTimeout(() => { $('copy-cmd').textContent = 'Copy command'; }, 2000);
    } catch { /* clipboard blocked; the text can still be selected */ }
  });

  // ---------- Settings ----------

  function fillSettings() {
    if (!state.settings) return;
    $('set-checkin').value = state.settings.checkInSeconds;
    $('set-inventory').value = state.settings.inventoryMinutes;
    $('set-jitter').value = state.settings.jitterPercent;
    $('set-tray').checked = state.settings.showTrayIcon;
  }

  $('settings-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    state.settings = await api('settings/agent', {
      method: 'PUT',
      body: JSON.stringify({
        ...state.settings,
        checkInSeconds: Number($('set-checkin').value),
        inventoryMinutes: Number($('set-inventory').value),
        jitterPercent: Number($('set-jitter').value),
        showTrayIcon: $('set-tray').checked,
      }),
    });
    fillSettings();
    $('settings-saved').hidden = false;
    setTimeout(() => { $('settings-saved').hidden = true; }, 2500);
  });

  // ---------- Audit ----------

  async function loadAudit() {
    const entries = await api('audit?take=200');
    $('audit').replaceChildren(...entries.map((a) => el('tr', {},
      el('td', {}, new Date(a.at).toLocaleString()), el('td', {}, a.actor), el('td', {}, a.action),
      el('td', {}, a.target ?? ''), el('td', {}, a.detail ?? ''))));
    if (state.server) {
      $('i-url').textContent = state.server.serverUrl;
      $('i-fp').textContent = state.server.fingerprint;
    }
  }

  // ---------- Theme ----------

  function applyTheme(dark) {
    document.documentElement.classList.toggle('dark', dark);
    $('theme').setAttribute('aria-label', dark ? 'Switch to light mode' : 'Switch to dark mode');
  }
  let dark = false;
  try { dark = localStorage.getItem('dh-theme') === 'dark'; } catch { /* storage unavailable */ }
  applyTheme(dark);
  $('theme').addEventListener('click', () => {
    dark = !dark;
    applyTheme(dark);
    try { localStorage.setItem('dh-theme', dark ? 'dark' : 'light'); } catch { /* ignore */ }
  });

  // ---------- Start ----------

  show(location.hash.slice(1) || 'console');
  loadDevices();
  setInterval(loadDevices, 30000);
})();
