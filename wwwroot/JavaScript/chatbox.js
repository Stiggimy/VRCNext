/* Custom Chatbox OSC */
let _cbLastUpdate = {};

function chatboxButtonHtml() {
    return chatboxEnabled
        ? `<span class="msi" style="font-size:16px;">stop</span> ${t('common.stop', 'Stop')}`
        : `<span class="msi" style="font-size:16px;">play_arrow</span> ${t('common.start', 'Start')}`;
}

function chatboxStatusText() {
    return chatboxEnabled
        ? t('chatbox.status.running', 'Running')
        : t('chatbox.status.not_running', 'Not running');
}

function chatboxPreviewFallback() {
    return chatboxEnabled
        ? t('chatbox.preview.waiting', 'Waiting for data...')
        : t('chatbox.preview.enable_prompt', 'Enable chatbox to see preview');
}

function syncChatboxToggleUi() {
    const btn = document.getElementById('cbConnBtn');
    const dot = document.getElementById('cbDot');
    const txt = document.getElementById('cbStatusText');
    if (btn) btn.innerHTML = chatboxButtonHtml();
    if (dot) dot.className = chatboxEnabled ? 'sf-dot online' : 'sf-dot offline';
    if (txt) txt.textContent = chatboxStatusText();
    if (typeof updateDashQuickControls === 'function') updateDashQuickControls();
}

function rerenderChatboxTranslations() {
    syncChatboxToggleUi();
    renderChatboxLines();
    handleChatboxUpdate(_cbLastUpdate || {});
}

document.documentElement.addEventListener('languagechange', rerenderChatboxTranslations);

function toggleChatbox() {
    chatboxEnabled = !chatboxEnabled;
    syncChatboxToggleUi();
    document.getElementById('badgeChatbox').className = chatboxEnabled ? 'mini-badge online' : 'mini-badge offline';
    updateChatboxConfig();
}

function updateChatboxConfig() {
    const showAfk = document.getElementById('cbShowAfk').checked;
    document.getElementById('cbAfkCard').style.display = showAfk ? '' : 'none';
    sendToCS({
        action: 'chatboxConfig',
        enabled: chatboxEnabled,
        showTime: document.getElementById('cbShowTime').checked,
        showMedia: document.getElementById('cbShowMedia').checked,
        showPlaytime: document.getElementById('cbShowPlaytime').checked,
        showCustomText: document.getElementById('cbShowCustom').checked,
        showSystemStats: document.getElementById('cbShowSystemStats').checked,
        showAfk: showAfk,
        afkMessage: document.getElementById('cbAfkMessage').value || t('chatbox.afk.default_message', 'Currently AFK'),
        suppressSound: document.getElementById('cbSuppressSound').checked,
        timeFormat: document.getElementById('cbTimeFormat').value,
        separator: document.getElementById('cbSeparator').value,
        intervalMs: parseInt(document.getElementById('cbInterval').value, 10) || 5000,
        customLines: chatboxCustomLines,
    });
}

function addChatboxLine() {
    const inp = document.getElementById('cbNewLine');
    const text = inp.value.trim();
    if (!text) return;
    chatboxCustomLines.push(text);
    inp.value = '';
    renderChatboxLines();
    updateChatboxConfig();
}

function removeChatboxLine(i) {
    chatboxCustomLines.splice(i, 1);
    renderChatboxLines();
    updateChatboxConfig();
}

function renderChatboxLines() {
    const el = document.getElementById('cbCustomLines');
    if (!el) return;
    if (chatboxCustomLines.length === 0) {
        el.innerHTML = `<div style="font-size:11px;color:var(--tx3);padding:6px 0;">${t('chatbox.custom_lines.empty', 'No custom lines added')}</div>`;
        return;
    }
    el.innerHTML = chatboxCustomLines.map((line, i) =>
        `<div class="cb-line-item">
            <span class="cb-line-text">${esc(line)}</span>
            <button class="cb-line-del" onclick="removeChatboxLine(${i})" title="${esc(t('common.remove', 'Remove'))}"><span class="msi" style="font-size:14px;">close</span></button>
        </div>`
    ).join('');
}

function handleChatboxUpdate(data) {
    _cbLastUpdate = { ..._cbLastUpdate, ...data };

    if (data.enabled !== undefined) {
        const wasEnabled = chatboxEnabled;
        chatboxEnabled = !!data.enabled;
        document.getElementById('badgeChatbox').className = chatboxEnabled ? 'mini-badge online' : 'mini-badge offline';
        syncChatboxToggleUi();
        if (chatboxEnabled !== wasEnabled) renderDashboard();
    }

    const previewText = document.getElementById('cbPreviewText');
    const charCount = document.getElementById('cbCharCount');
    const text = _cbLastUpdate.chatboxText || '';
    if (previewText && charCount) {
        if (text) {
            previewText.textContent = text;
            charCount.textContent = text.length;
            charCount.style.color = text.length > 130 ? 'var(--err)' : 'var(--tx3)';
        } else {
            previewText.textContent = chatboxPreviewFallback();
            charCount.textContent = '0';
            charCount.style.color = 'var(--tx3)';
        }
    }

    const mediaInfo = document.getElementById('cbMediaInfo');
    if (!mediaInfo) return;
    if (_cbLastUpdate.isPlaying && _cbLastUpdate.currentTitle) {
        const pos = formatMediaTime(_cbLastUpdate.positionMs || 0);
        const dur = formatMediaTime(_cbLastUpdate.durationMs || 0);
        const progress = (_cbLastUpdate.durationMs || 0) > 0
            ? ((_cbLastUpdate.positionMs || 0) / _cbLastUpdate.durationMs * 100)
            : 0;
        mediaInfo.innerHTML = `
            <div class="cb-media-now-playing">
                <span class="msi" style="font-size:16px;color:var(--accent);">music_note</span>
                <div class="cb-media-details">
                    <div class="cb-media-title">${esc(_cbLastUpdate.currentTitle)}</div>
                    <div class="cb-media-artist">${esc(_cbLastUpdate.currentArtist || t('chatbox.media.unknown_artist', 'Unknown'))}</div>
                </div>
            </div>
            <div class="cb-progress-bar"><div class="cb-progress-fill" style="width:${progress}%"></div></div>
            <div class="cb-media-time">${pos} / ${dur}</div>`;
    } else {
        mediaInfo.innerHTML = `<div class="cb-media-idle"><span class="msi" style="font-size:16px;vertical-align:middle;">music_off</span> ${t('chatbox.media.none', 'No media playing')}</div>`;
    }
}

function formatMediaTime(ms) {
    const s = Math.floor(ms / 1000);
    const m = Math.floor(s / 60);
    const sec = s % 60;
    if (m >= 60) {
        const h = Math.floor(m / 60);
        return `${h}:${String(m % 60).padStart(2, '0')}:${String(sec).padStart(2, '0')}`;
    }
    return `${m}:${String(sec).padStart(2, '0')}`;
}
