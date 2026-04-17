/* === Update UI === */

let _updatePanelOpen = false;
let _updateInstalling = false;
let _updateVersion = '';
let _updatePhase = 'idle';
let _updatePct = 0;

function updButtonHtml(icon, key, fallback) {
    return `<span class="msi" style="font-size:16px;">${icon}</span> ${t(key, fallback)}`;
}

function updStatusText() {
    if (_updatePhase === 'starting') return t('update.starting', 'Starting...');
    if (_updatePhase === 'downloading') return tf('update.downloading_progress', { percent: _updatePct }, 'Downloading... {percent}%');
    if (_updatePhase === 'installing') return t('update.installing_restarting', 'Installing & restarting...');
    return t('update.downloading', 'Downloading...');
}

function rerenderUpdateTranslations() {
    const btn = document.getElementById('updBtn');
    const status = document.getElementById('updStatus');
    if (btn) {
        if (_updatePhase === 'installing') btn.innerHTML = updButtonHtml('restart_alt', 'update.restarting', 'Restarting...');
        else if (_updatePhase === 'starting') btn.innerHTML = updButtonHtml('hourglass_empty', 'update.starting', 'Starting...');
        else btn.innerHTML = updButtonHtml('download', 'update.download_install', 'Download & Install');
    }
    if (status && document.getElementById('updProgressWrap')?.style.display !== 'none') {
        status.textContent = updStatusText();
    }
}

document.documentElement.addEventListener('languagechange', rerenderUpdateTranslations);

function showUpdateAvailable(version) {
    _updateVersion = version;
    _updatePhase = 'ready';
    _updatePct = 0;
    document.getElementById('updVersion').textContent = version;
    document.getElementById('btnUpdate').style.display = '';
    setUpdProgress(false);
    document.getElementById('updBtn').disabled = false;
    rerenderUpdateTranslations();
    _setUpdCardAvailable(version);
}

// Settings card helpers.
function _setUpdCardStatus(text) {
    const el = document.getElementById('setUpdStatus');
    if (el) el.textContent = text;
}
function _setUpdCardAvailable(version) {
    const wrap = document.getElementById('setUpdInstallWrap');
    const ver  = document.getElementById('setUpdVersion');
    if (wrap) wrap.style.display = '';
    if (ver)  ver.textContent = version;
    _setUpdCardStatus('');
}
function _setUpdCardProgress(pct) {
    const wrap = document.getElementById('setUpdProgressWrap');
    const fill = document.getElementById('setUpdProgressFill');
    const lbl  = document.getElementById('setUpdProgressStatus');
    const btn  = document.getElementById('setUpdInstallBtn');
    if (wrap) wrap.style.display = '';
    if (fill) fill.style.width = pct + '%';
    if (btn)  btn.disabled = true;
    if (lbl)  lbl.textContent = pct < 100
        ? `${t('update.downloading','Downloading...')} ${pct}%`
        : t('update.installing_restarting','Installing & restarting...');
}

function settingsCheckUpdate() {
    const btn = document.getElementById('setUpdCheckBtn');
    if (btn) { btn.disabled = true; }
    _setUpdCardStatus(t('update.checking', 'Checking...'));
    sendToCS({ action: 'checkUpdate' });
    // re-enable after a few seconds in case nothing found
    setTimeout(() => {
        if (btn) btn.disabled = false;
        const wrap = document.getElementById('setUpdInstallWrap');
        if (!wrap || wrap.style.display === 'none')
            _setUpdCardStatus(t('update.up_to_date', 'You are up to date.'));
    }, 4000);
}

function settingsInstallUpdate() {
    if (_updateInstalling) return;
    _updateInstalling = true;
    _updatePhase = 'starting';
    _updatePct = 0;
    const btn = document.getElementById('updBtn');
    if (btn) { btn.disabled = true; btn.innerHTML = updButtonHtml('hourglass_empty','update.starting','Starting...'); }
    _setUpdCardProgress(0);
    rerenderUpdateTranslations();
    setUpdProgress(true, 0);
    sendToCS({ action: 'installUpdate' });
}

let _updDismiss = null;

function toggleUpdatePanel() {
    if (_updateInstalling) return;
    _updatePanelOpen = !_updatePanelOpen;
    const panel = document.getElementById('updatePanel');
    if (_updatePanelOpen) {
        panel.style.display = '';
        requestAnimationFrame(() => panel.classList.add('panel-open'));
        // close other panels
        const np = document.getElementById('notifPanel');
        if (np?.classList.contains('panel-open')) toggleNotifPanel();
        const cp = document.getElementById('chatPanel');
        if (cp?.classList.contains('panel-open')) toggleChatPanel();
        setTimeout(() => {
            _updDismiss = e => {
                const p = document.getElementById('updatePanel');
                const b = document.getElementById('btnUpdate');
                if (!p?.contains(e.target) && !b?.contains(e.target)) toggleUpdatePanel();
            };
            document.addEventListener('click', _updDismiss);
        }, 0);
    } else {
        if (_updDismiss) { document.removeEventListener('click', _updDismiss); _updDismiss = null; }
        panel.classList.remove('panel-open');
        setTimeout(() => { if (!_updatePanelOpen) panel.style.display = 'none'; }, 90);
    }
}

function startUpdate() {
    if (_updateInstalling) return;
    _updateInstalling = true;
    _updatePhase = 'starting';
    _updatePct = 0;
    document.getElementById('updBtn').disabled = true;
    rerenderUpdateTranslations();
    setUpdProgress(true, 0);
    _setUpdCardProgress(0);
    sendToCS({ action: 'installUpdate' });
}

function setUpdProgress(visible, pct = 0) {
    _updatePct = pct;
    document.getElementById('updProgressWrap').style.display = visible ? '' : 'none';
    document.getElementById('updProgressFill').style.width = pct + '%';
    document.getElementById('updStatus').textContent = updStatusText();
}

function onUpdateProgress(pct) {
    _updatePhase = 'downloading';
    setUpdProgress(true, pct);
    _setUpdCardProgress(pct);
}

function onUpdateReady() {
    _updatePhase = 'installing';
    _updatePct = 100;
    setUpdProgress(true, 100);
    rerenderUpdateTranslations();
    _setUpdCardProgress(100);
}
