// DeviceHaven Console. Plain JavaScript, no outside libraries or network calls.
// Everything shown comes from this server's admin API. Text from the server is
// always inserted with textContent, never as HTML.
(() => {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const state = { devices: [], settings: null, server: null, updates: [] };

  // ---------- Helpers ----------

  async function api(path, options = {}) {
    const res = await fetch(`/api/admin/${path}`, {
      ...options,
      headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
    });
    if (res.status === 403) {
      showNotice('The console only works when opened on the server itself (https://localhost) until admin sign-in is added.', true);
      throw new Error('forbidden');
    }
    if (!res.ok && res.status !== 204) {
      let message = `Request failed (${res.status})`;
      try { message = (await res.json()).message || message; } catch { /* not JSON */ }
      showNotice(message);
      throw new Error(message);
    }
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

  let noticeTimer;
  function showNotice(text, sticky = false) {
    const n = $('notice');
    n.textContent = text;
    n.hidden = false;
    clearTimeout(noticeTimer);
    if (!sticky) noticeTimer = setTimeout(() => { n.hidden = true; }, 7000);
  }

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
    updates: ['Updates', 'App and Windows updates waiting on your PCs'],
    messages: ['Messages & restarts', 'Tell users something, or ask PCs to restart'],
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
    if (view === 'updates') loadUpdates();
    if (view === 'messages') fillTargets();
  }

  window.addEventListener('hashchange', () => show(location.hash.slice(1)));

  // ---------- Console ----------

  async function loadDevices() {
    try {
      [state.devices, state.settings, state.server, state.updates] =
        await Promise.all([api('devices'), api('settings/agent'), api('server'), api('updates')]);
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
    const waiting = state.updates.length;
    const pcs = new Set(state.updates.flatMap((u) => u.devices.map((d) => d.id))).size;
    $('s-updates').textContent = waiting;
    $('s-updates-note').textContent = waiting ? `On ${pcs} PC${pcs === 1 ? '' : 's'}` : 'All up to date';
    $('nav-updates').textContent = waiting;
    $('nav-updates').hidden = waiting === 0;

    const term = $('search').value.trim().toLowerCase();
    const rows = active.filter((d) => !term || `${d.hostname} ${d.osName} ${d.primaryIp ?? ''} ${d.lastIp ?? ''}`.toLowerCase().includes(term));
    const body = $('devices');
    body.replaceChildren(...rows.map((d) => {
      const on = isOnline(d);
      return el('tr', {},
        el('td', { class: 'name' }, d.hostname),
        el('td', {}, el('span', { class: `status ${on ? 'ok' : 'bad'}` }, on ? 'Online' : 'Offline')),
        el('td', {}, d.osName),
        el('td', {}, d.agentVersion),
        el('td', {}, ago(d.lastSeen)),
        el('td', {}, d.primaryIp ?? d.lastIp ?? '–'),
        el('td', {}, el('button', { type: 'button', class: 'btn small', onclick: () => openDevice(d.id) }, 'Details')));
    }));
    $('empty').hidden = active.length > 0;
  }

  $('search').addEventListener('input', renderDevices);

  // ---------- Updates ----------

  function statusPill(devices) {
    const approved = devices.filter((d) => d.approvalStatus === 'approved').length;
    const failed = devices.filter((d) => d.approvalStatus === 'failed');
    if (failed.length) return el('span', { class: 'pill failed', title: failed.map((d) => `${d.hostname}: ${d.approvalMessage ?? ''}`).join('\n') }, `Failed on ${failed.length}`);
    if (approved === devices.length) return el('span', { class: 'pill approved' }, 'Approved, installing');
    if (approved) return el('span', { class: 'pill approved' }, `Approved on ${approved}`);
    return el('span', { class: 'pill' }, 'Waiting for you');
  }

  function severityPill(severity) {
    if (!severity) return '–';
    return el('span', { class: `pill ${severity.toLowerCase()}` }, severity);
  }

  async function approve(items) {
    const r = await api('updates/approve', { method: 'POST', body: JSON.stringify({ items }) });
    showNotice(r.installs ? `Approved ${r.installs} install${r.installs === 1 ? '' : 's'}. They start at each PC's next check-in.` : 'Those updates were already approved.');
    await loadDevices();
    loadUpdates();
  }

  async function loadUpdates() {
    state.updates = await api('updates');
    const apps = state.updates.filter((u) => u.kind === 'app');
    const wins = state.updates.filter((u) => u.kind === 'windows');
    const devicesCell = (u) => el('td', { title: u.devices.map((d) => d.hostname).join(', ') },
      u.devices.length === 1 ? u.devices[0].hostname : `${u.devices.length} PCs`);
    const approveBtn = (u) => u.devices.every((d) => d.approvalStatus === 'approved') ? '' :
      el('button', { type: 'button', class: 'btn small', onclick: () => approve([{ kind: u.kind, key: u.key }]) }, 'Approve');

    $('app-updates').replaceChildren(...apps.map((u) => el('tr', {},
      el('td', { class: 'name' }, u.title),
      el('td', {}, `${u.devices[0]?.currentVersion ?? '?'} → ${u.availableVersion ?? '?'}`),
      devicesCell(u), el('td', {}, statusPill(u.devices)), el('td', {}, approveBtn(u)))));
    $('windows-updates').replaceChildren(...wins.map((u) => el('tr', {},
      el('td', { class: 'name' }, u.title), el('td', {}, u.kb ?? '–'), el('td', {}, severityPill(u.severity)),
      devicesCell(u), el('td', {}, statusPill(u.devices)), el('td', {}, approveBtn(u)))));

    $('no-app-updates').hidden = apps.length > 0;
    $('no-windows-updates').hidden = wins.length > 0;
    const pendingApps = apps.filter((u) => u.devices.some((d) => d.approvalStatus !== 'approved'));
    const pendingWins = wins.filter((u) => u.devices.some((d) => d.approvalStatus !== 'approved'));
    $('approve-all-apps').hidden = pendingApps.length < 2;
    $('approve-all-windows').hidden = pendingWins.length < 2;
    $('approve-all-apps').onclick = () => approve(pendingApps.map((u) => ({ kind: u.kind, key: u.key })));
    $('approve-all-windows').onclick = () => approve(pendingWins.map((u) => ({ kind: u.kind, key: u.key })));
  }

  $('scan-all').addEventListener('click', async () => {
    const r = await api('commands/broadcast', { method: 'POST', body: JSON.stringify({ kind: 'updates.scan' }) });
    showNotice(`Asked ${r.devices} PC${r.devices === 1 ? '' : 's'} to look for updates. Results appear here within a few minutes.`);
  });

  // ---------- Messages and restarts ----------

  function fillTargets() {
    for (const id of ['msg-targets', 'restart-targets']) {
      const active = state.devices.filter((d) => !d.revoked);
      $(id).replaceChildren(
        el('label', {}, el('input', { type: 'checkbox', value: 'all', checked: 'checked', 'data-all': '1' }), `All devices (${active.length})`),
        ...active.map((d) => el('label', {}, el('input', { type: 'checkbox', value: d.id }), d.hostname)));
    }
  }

  function chosenDevices(containerId) {
    const boxes = [...$(containerId).querySelectorAll('input[type=checkbox]')];
    if (boxes.find((b) => b.dataset.all)?.checked) return null;   // all
    return boxes.filter((b) => b.checked && !b.dataset.all).map((b) => b.value);
  }

  $('message-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const deviceIds = chosenDevices('msg-targets');
    if (deviceIds && deviceIds.length === 0) { showNotice('Pick at least one PC.'); return; }
    const r = await api('commands/broadcast', { method: 'POST', body: JSON.stringify({
      kind: 'notify', title: $('msg-title').value.trim() || null, message: $('msg-body').value.trim(), deviceIds }) });
    $('msg-sent').textContent = `Sent to ${r.devices} PC${r.devices === 1 ? '' : 's'}`;
    $('msg-sent').hidden = false;
    $('msg-body').value = '';
    setTimeout(() => { $('msg-sent').hidden = true; }, 4000);
  });

  $('restart-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const deviceIds = chosenDevices('restart-targets');
    if (deviceIds && deviceIds.length === 0) { showNotice('Pick at least one PC.'); return; }
    const r = await api('commands/broadcast', { method: 'POST', body: JSON.stringify({
      kind: 'restart.request', deadlineMinutes: Number($('restart-within').value), message: $('restart-reason').value.trim() || null, deviceIds }) });
    $('restart-sent').textContent = `Restart requested on ${r.devices} PC${r.devices === 1 ? '' : 's'}`;
    $('restart-sent').hidden = false;
    setTimeout(() => { $('restart-sent').hidden = true; }, 4000);
  });

  // ---------- Device details ----------

  async function sendCommand(id, body, doneText) {
    await api(`devices/${id}/commands`, { method: 'POST', body: JSON.stringify(body) });
    showNotice(doneText);
    openDevice(id);
  }

  const jobNames = {
    'inventory.scan': 'Scan inventory', 'updates.scan': 'Look for updates', 'apps.upgrade': 'Install app updates',
    'windows.install': 'Install Windows updates', 'notify': 'Message', 'restart.request': 'Restart request',
  };

  async function openDevice(id) {
    const [detail, upd, jobs] = await Promise.all([api(`devices/${id}`), api(`devices/${id}/updates`), api(`devices/${id}/commands`)]);
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
        fact('IP address', d.primaryIp ?? inv?.primaryIpv4),
        fact('Connected from', d.lastIp),
        fact('Inventory updated', detail.inventoryAt ? ago(detail.inventoryAt) : 'Not yet')),
    ];
    // Actions
    const msgInput = el('input', { type: 'text', maxlength: '500', placeholder: 'Message to show on this PC' });
    const restartSelect = el('select', {}, ...[['30', '30 minutes'], ['120', '2 hours'], ['240', '4 hours'], ['1440', '24 hours']]
      .map(([v, t]) => el('option', v === '240' ? { value: v, selected: 'selected' } : { value: v }, t)));
    parts.push(el('div', { class: 'actions' },
      el('button', { type: 'button', class: 'btn small', onclick: async () => {
        await api(`devices/${id}/commands`, { method: 'POST', body: JSON.stringify({ kind: 'inventory.scan' }) });
        sendCommand(id, { kind: 'updates.scan' }, `${d.hostname} will rescan at its next check-in (within ${state.settings.checkInSeconds} seconds).`);
      } }, 'Scan now'),
    ));
    parts.push(el('div', { class: 'inline-form' }, msgInput,
      el('button', { type: 'button', class: 'btn small', onclick: () => {
        if (!msgInput.value.trim()) return;
        sendCommand(id, { kind: 'notify', message: msgInput.value.trim() }, `Message sent to ${d.hostname}.`);
      } }, 'Send message')));
    parts.push(el('div', { class: 'inline-form' }, el('span', {}, 'Restart within'), restartSelect,
      el('button', { type: 'button', class: 'btn small', onclick: () =>
        sendCommand(id, { kind: 'restart.request', deadlineMinutes: Number(restartSelect.value) }, `Restart requested on ${d.hostname}.`) }, 'Request restart')));

    // Updates
    parts.push(el('h3', {}, `Updates waiting (${upd.updates.length})`));
    if (upd.rebootPending) parts.push(el('div', { class: 'callout warn' }, 'Windows says this PC needs a restart to finish installing updates.'));
    if (upd.error) parts.push(el('div', { class: 'callout warn' }, upd.error));
    parts.push(el('p', { class: 'muted small' }, upd.scannedAt ? `Last checked ${ago(upd.scannedAt)}` : 'Not checked yet. Choose Scan now.'));
    if (upd.updates.length) {
      const waiting = upd.updates.filter((u) => u.approvalStatus !== 'approved');
      if (waiting.length > 1) parts.push(el('div', {}, el('button', { type: 'button', class: 'btn small primary', onclick: async () => {
        await approve(waiting.map((u) => ({ kind: u.kind, key: u.key, deviceIds: [id] })));
        openDevice(id);
      } }, `Approve all ${waiting.length}`)));
      parts.push(el('div', { class: 'table-wrap' }, el('table', {},
        el('thead', {}, el('tr', {}, el('th', {}, 'Update'), el('th', {}, 'Type'), el('th', {}, 'Details'), el('th', {}, 'Status'), el('th', {}, ''))),
        el('tbody', {}, ...upd.updates.map((u) => el('tr', {},
          el('td', { class: 'name' }, u.title),
          el('td', {}, u.kind === 'app' ? 'App' : 'Windows'),
          el('td', {}, u.kind === 'app' ? `${u.currentVersion ?? '?'} → ${u.availableVersion ?? '?'}` : [u.kb, u.severity].filter(Boolean).join(' · ') || '–'),
          el('td', {}, u.approvalStatus === 'approved' ? el('span', { class: 'pill approved' }, 'Approved')
            : u.approvalStatus === 'failed' ? el('span', { class: 'pill failed', title: u.approvalMessage ?? '' }, 'Failed') : el('span', { class: 'pill' }, 'Waiting')),
          el('td', {}, u.approvalStatus === 'approved' ? '' : el('button', { type: 'button', class: 'btn small', onclick: async () => {
            await approve([{ kind: u.kind, key: u.key, deviceIds: [id] }]);
            openDevice(id);
          } }, 'Approve'))))))));
    }

    if (inv) {
      parts.push(el('h3', {}, 'Disks'));
      parts.push(el('div', { class: 'table-wrap' }, el('table', {},
        el('thead', {}, el('tr', {}, el('th', {}, 'Drive'), el('th', {}, 'Size'), el('th', {}, 'Free'), el('th', {}, 'BitLocker'))),
        el('tbody', {}, ...inv.disks.map((k) => el('tr', {},
          el('td', {}, k.name), el('td', {}, bytes(k.sizeBytes)), el('td', {}, bytes(k.freeBytes)),
          el('td', {}, k.bitLockerOn == null ? 'Unknown' : k.bitLockerOn ? 'On' : 'Off')))))));
      if (inv.networks?.length) {
        parts.push(el('h3', {}, 'Network'));
        parts.push(el('div', { class: 'table-wrap' }, el('table', {},
          el('thead', {}, el('tr', {}, el('th', {}, 'Adapter'), el('th', {}, 'Type'), el('th', {}, 'IPv4'), el('th', {}, 'Gateway'), el('th', {}, 'MAC address'), el('th', {}, 'Speed'))),
          el('tbody', {}, ...inv.networks.map((n) => el('tr', {},
            el('td', {}, n.name), el('td', {}, n.type), el('td', {}, n.ipv4?.join(', ') || '–'),
            el('td', {}, n.gateway ?? '–'), el('td', {}, n.macAddress ?? '–'),
            el('td', {}, n.speedMbps ? (n.speedMbps >= 1000 ? `${n.speedMbps / 1000} Gbps` : `${n.speedMbps} Mbps`) : '–')))))));
      }
      parts.push(el('h3', {}, `Installed programs (${inv.apps.length})`));
      parts.push(el('div', { class: 'table-wrap' }, el('table', {},
        el('thead', {}, el('tr', {}, el('th', {}, 'Name'), el('th', {}, 'Version'), el('th', {}, 'Publisher'))),
        el('tbody', {}, ...inv.apps.map((a) => el('tr', {},
          el('td', {}, a.name), el('td', {}, a.version ?? '–'), el('td', {}, a.publisher ?? '–')))))));
    }
    if (jobs.length) {
      parts.push(el('h3', {}, 'Recent jobs'));
      parts.push(el('div', { class: 'table-wrap' }, el('table', {},
        el('thead', {}, el('tr', {}, el('th', {}, 'When'), el('th', {}, 'Job'), el('th', {}, 'Status'), el('th', {}, 'Result'))),
        el('tbody', {}, ...jobs.slice(0, 15).map((j) => el('tr', {},
          el('td', {}, ago(j.createdAt)), el('td', {}, jobNames[j.kind] ?? j.kind),
          el('td', {}, { pending: 'Waiting for check-in', sent: 'Running', done: 'Done', failed: 'Failed' }[j.status] ?? j.status),
          el('td', {}, j.result ?? '')))))));
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
    $('set-updatescan').value = state.settings.updateScanHours;
    $('set-restart').value = state.settings.restartDeadlineHours;
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
        updateScanHours: Number($('set-updatescan').value),
        restartDeadlineHours: Number($('set-restart').value),
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
