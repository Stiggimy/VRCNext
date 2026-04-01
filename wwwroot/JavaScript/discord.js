// Discord Rich Presence
let _dpRunning = false;
let _dpJoinedAt = null;
let _dpTickInterval = null;
let _dpCurrentWorld = '';
let _dpCurrentImg = '';
let _dpCurrentState = '';

function dpButtonHtml() {
    return _dpRunning
        ? `<span class="msi" style="font-size:16px;">stop</span> ${t('common.stop', 'Stop')}`
        : `<span class="msi" style="font-size:16px;">play_arrow</span> ${t('common.start', 'Start')}`;
}

function dpStatusText() {
    return _dpRunning
        ? t('discord.status.connected', 'Connected to Discord')
        : t('discord.status.not_connected', 'Not connected');
}

function dpElapsedText(sec) {
    const h = Math.floor(sec / 3600);
    const m = Math.floor((sec % 3600) / 60);
    const s = sec % 60;
    const time = h > 0
        ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`
        : `${m}:${String(s).padStart(2, '0')}`;
    return `${time} ${t('discord.preview.elapsed', 'elapsed')}`;
}

function dpSyncUi() {
    const dot = document.getElementById('dpDot');
    const txt = document.getElementById('dpStatusText');
    const btn = document.getElementById('dpConnBtn');
    const preview = document.getElementById('dpPreviewCard');
    if (dot) dot.className = _dpRunning ? 'sf-dot online' : 'sf-dot offline';
    if (txt) txt.textContent = dpStatusText();
    if (btn) btn.innerHTML = dpButtonHtml();
    if (preview) preview.style.display = _dpRunning ? '' : 'none';
}

function rerenderDiscordTranslations() {
    dpSyncUi();
    dpUpdatePreviewClock();
}

document.documentElement.addEventListener('languagechange', rerenderDiscordTranslations);

document.documentElement.addEventListener('tabchange', () => {
    const tab = document.getElementById('tab19');
    if (tab && tab.classList.contains('active')) {
        dpUpdatePreviewClock();
    }
});

function dpAutoSave() {
    if (typeof saveSettings === 'function') saveSettings();
}

function dpToggle() {
    if (_dpRunning) sendToCS({ action: 'dpStop' });
    else sendToCS({ action: 'dpStart' });
}

function dpOnState(p) {
    _dpRunning = !!p.running;
    dpSyncUi();
    if (typeof updateDashQuickControls === 'function') updateDashQuickControls();
    if (_dpRunning) {
        if (!_dpJoinedAt) _dpJoinedAt = Date.now();
        dpUpdateStatusDot();
        dpStartClock();
    } else {
        dpStopClock();
    }
}

function dpOnInstanceUpdate(worldName, worldImg, instanceState, joinedAt) {
    _dpCurrentWorld = worldName || '';
    _dpCurrentImg = worldImg || '';
    _dpCurrentState = instanceState || '';
    if (joinedAt) _dpJoinedAt = new Date(joinedAt).getTime();

    const title = document.getElementById('dpPreviewTitle');
    const state = document.getElementById('dpPreviewState');
    const img = document.getElementById('dpPreviewImg');
    const ph = document.getElementById('dpPreviewImgPlaceholder');
    if (title) title.textContent = _dpCurrentWorld || '-';
    if (state) state.textContent = _dpCurrentState || '-';
    if (img) {
        if (_dpCurrentImg) {
            img.src = _dpCurrentImg;
            img.style.display = '';
            if (ph) ph.style.display = 'none';
        } else {
            img.style.display = 'none';
            if (ph) ph.style.display = '';
        }
    }

    dpUpdateStatusDot();
    if (_dpRunning) dpUpdatePreviewClock();
}

function dpClearPresencePreview() {
    _dpCurrentWorld = '';
    _dpCurrentImg = '';
    _dpCurrentState = '';
    _dpJoinedAt = null;

    const title = document.getElementById('dpPreviewTitle');
    const state = document.getElementById('dpPreviewState');
    const img = document.getElementById('dpPreviewImg');
    const ph = document.getElementById('dpPreviewImgPlaceholder');
    if (title) title.textContent = '-';
    if (state) state.textContent = '-';
    if (img) img.style.display = 'none';
    if (ph) ph.style.display = '';

    const el = document.getElementById('dpPreviewTime');
    if (el) el.textContent = '-';
}

function dpUpdateStatusDot() {
    const dot = document.getElementById('dpPreviewStatusDot');
    if (!dot) return;
    const status = ((typeof currentVrcUser !== 'undefined' && currentVrcUser?.status) || '').toLowerCase();
    const cls = status === 'join me' ? 'join_me'
        : status === 'busy' ? 'busy'
        : status === 'ask me' ? 'ask_me'
        : status === 'offline' ? 'offline'
        : 'online';
    dot.className = 'dp-status-dot ' + cls;
}

function dpStartClock() {
    dpStopClock();
    _dpTickInterval = setInterval(dpUpdatePreviewClock, 1000);
}

function dpStopClock() {
    if (_dpTickInterval) {
        clearInterval(_dpTickInterval);
        _dpTickInterval = null;
    }
}

function dpUpdatePreviewClock() {
    const el = document.getElementById('dpPreviewTime');
    if (!el) return;
    if (!_dpJoinedAt) {
        el.textContent = '-';
        return;
    }
    const sec = Math.floor((Date.now() - _dpJoinedAt) / 1000);
    el.textContent = dpElapsedText(sec);
}
