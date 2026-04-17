/* Messenger
 * Chat over VRChat invite message slots.
 */

const MSGR_MAX_CHARS = 60;
const MSGR_SEND_COOLDOWN = 45; // seconds

// ── Shared Content Cards ──────────────────────────────────────────
const MSGR_VRC_ID_RE = /^((wrld|avtr|grp|usr|evnt)_[a-zA-Z0-9-]+)$/;
const MSGR_CONTENT_META = {
    wrld: { typeKey: 'messenger.shared.type_world',   typeFb: 'World',   icon: 'travel_explore', openKey: 'messenger.shared.open_world',   openFb: 'Open World'   },
    avtr: { typeKey: 'messenger.shared.type_avatar',  typeFb: 'Avatar',  icon: 'checkroom',      openKey: 'messenger.shared.open_avatar',  openFb: 'Open Avatar'  },
    grp:  { typeKey: 'messenger.shared.type_group',   typeFb: 'Group',   icon: 'group',          openKey: 'messenger.shared.open_group',   openFb: 'Open Group'   },
    usr:  { typeKey: 'messenger.shared.type_profile', typeFb: 'Profile', icon: 'person',         openKey: 'messenger.shared.open_profile', openFb: 'Open Profile' },
    evnt: { typeKey: 'messenger.shared.type_event',   typeFb: 'Event',   icon: 'event',          openKey: 'messenger.shared.open_event',   openFb: 'Open Event'   },
};
const _msgrContentCache = {}; // id -> { name, image }

function msgrParseContentId(text) {
    if (!text) return null;
    const m = (text || '').trim().match(MSGR_VRC_ID_RE);
    return m ? { id: m[1], prefix: m[2].toLowerCase() } : null;
}

function msgrContentOpen(id, prefix) {
    if (prefix === 'wrld' && typeof openWorldSearchDetail === 'function') return openWorldSearchDetail(id);
    if (prefix === 'avtr' && typeof openAvatarDetail     === 'function') return openAvatarDetail(id);
    if (prefix === 'grp'  && typeof openGroupDetail      === 'function') return openGroupDetail(id);
    if (prefix === 'usr'  && typeof openUserDetail       === 'function') return openUserDetail(id);
    if (prefix === 'evnt' && typeof openEventDetail      === 'function') return openEventDetail(id);
}

function msgrBuildContentCard(id, prefix, time) {
    const meta     = MSGR_CONTENT_META[prefix] || MSGR_CONTENT_META.wrld;
    const typeLabel = t(meta.typeKey, meta.typeFb);
    const openLabel = t(meta.openKey, meta.openFb);
    const cached   = _msgrContentCache[id];
    const name     = cached?.name  || esc(id);
    const imgSrc   = cached?.image || '';
    const hasImage = !!imgSrc;
    return `<div class="vrcn-shared-content-message" data-content-id="${esc(id)}" data-content-prefix="${esc(prefix)}">
        <div class="scm-thumb${hasImage ? ' scm-has-image' : ' scm-loading'}">
            <img alt="" src="${esc(imgSrc)}"${hasImage ? ' class="scm-img-loaded"' : ''}
                onload="this.classList.add('scm-img-loaded');this.closest('.scm-thumb').classList.add('scm-has-image');this.closest('.scm-thumb').classList.remove('scm-loading');"
                onerror="this.style.display='none';">
            <span class="msi scm-thumb-icon">${esc(meta.icon)}</span>
        </div>
        <div class="scm-body">
            <div class="scm-type-row"><span class="msi">${esc(meta.icon)}</span>${esc(typeLabel)}</div>
            <div class="scm-name">${name}</div>
            <button class="scm-open-btn" onclick="msgrContentOpen('${jsq(id)}','${jsq(prefix)}')">
                <span class="msi">open_in_new</span>${esc(openLabel)}
            </button>
        </div>
    </div>
    <div class="msgr-time">${esc(time)}</div>`;
}

function msgrFillContentCard(id, prefix, cardEl) {
    if (!cardEl) return;

    // 1 – try local JS caches (same pattern for all content types)
    let name = '', image = '';

    if (prefix === 'wrld' && typeof worldInfoCache !== 'undefined' && worldInfoCache[id]) {
        const w = worldInfoCache[id];
        name  = w.name  || '';
        image = w.thumbnailImageUrl || w.imageUrl || '';
    } else if (prefix === 'avtr' && typeof avatarInfoCache !== 'undefined' && avatarInfoCache[id]) {
        const a = avatarInfoCache[id];
        name  = a.name  || '';
        image = a.thumbnailImageUrl || a.imageUrl || '';
    } else if (prefix === 'usr' && typeof vrcFriendsData !== 'undefined') {
        const f = vrcFriendsData.find(x => x.id === id);
        if (f) { name = f.displayName || ''; image = f.image || ''; }
    } else if (prefix === 'grp' && typeof myGroups !== 'undefined') {
        const g = myGroups.find(x => x.id === id);
        if (g) { name = g.name || ''; image = g.iconUrl || g.bannerUrl || ''; }
    }

    // Fallback: in-memory session cache populated by previous C# responses / prefetch
    if (!name && !image && _msgrContentCache[id]) {
        name  = _msgrContentCache[id].name  || '';
        image = _msgrContentCache[id].image || '';
    }

    if (name || image) {
        _msgrContentCache[id] = { name, image };
        msgrApplyContentCard(id, name, image);
        return;
    }

    // 2 – request from C#
    sendToCS({ action: 'vrcGetSharedContentInfo', contentId: id, contentType: prefix });
}

function msgrApplyContentCard(id, name, image) {
    document.querySelectorAll(`.vrcn-shared-content-message[data-content-id="${CSS.escape(id)}"]`).forEach(card => {
        const nameEl = card.querySelector('.scm-name');
        if (nameEl && name) nameEl.textContent = name;

        const thumb = card.querySelector('.scm-thumb');
        const img   = card.querySelector('.scm-thumb img');
        if (thumb && img && image) {
            img.style.display = '';
            img.src = image;
            thumb.classList.remove('scm-loading');
            img.onload  = () => { img.classList.add('scm-img-loaded'); thumb.classList.add('scm-has-image'); };
            img.onerror = () => { img.style.display = 'none'; };
        }
    });
}

function handleSharedContentInfo(payload) {
    if (!payload?.contentId) return;
    const { contentId, contentType, name, image } = payload;
    if (name || image) {
        const n = name || '', im = image || '';
        _msgrContentCache[contentId] = { name: n, image: im };
        if (contentType === 'avtr' && typeof avatarInfoCache !== 'undefined')
            avatarInfoCache[contentId] = { id: contentId, name: n, thumbnailImageUrl: im };
        else if (contentType === 'wrld' && typeof worldInfoCache !== 'undefined')
            worldInfoCache[contentId] = { id: contentId, name: n, thumbnailImageUrl: im };
        msgrApplyContentCard(contentId, n, im);
    }
}
// ─────────────────────────────────────────────────────────────────

let _messengerUserId = null;
let _messengerName = '';
let _messengerImage = '';
let _messengerStatus = '';
let _messengerStatusDesc = '';
let _messengerCooldown = null;
let _messengerSlots = { used: 0, total: 24 };
let _messengerHistory = [];
let _pendingBoopUserId = null;
let _msgrCdInterval = null;
let _msgrCdEnd = 0; // Date.now() timestamp when cooldown expires

// Chat inbox: userId -> { userId, displayName, image, status, statusDesc, text, time, count }
const _chatInbox = new Map();
let _chatPanelDismiss = null;

function msgrFormatTime(value) {
    const dt = new Date(value);
    if (!dt || isNaN(dt)) return '';
    const now   = new Date();
    const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    const msgDay = new Date(dt.getFullYear(), dt.getMonth(), dt.getDate());
    const diffDays = Math.round((today - msgDay) / 86400000);
    const timeStr = fmtTime(dt);
    if (diffDays === 0) return t('messenger.date.today', 'Today') + ' · ' + timeStr;
    if (diffDays === 1) return t('messenger.date.yesterday', 'Yesterday') + ' · ' + timeStr;
    return fmtShortDate(dt) + ' · ' + timeStr;
}

function msgrStatusText(status, statusDesc) {
    return statusDesc || statusLabel(status) || t('status.offline', 'Offline');
}

function msgrSlotTitle() {
    return t('messenger.slots.title', 'Message slots used (60 min cooldown each)');
}

function msgrInputPlaceholder(name) {
    return tf('messenger.input.placeholder', { name }, `Message ${name}...`);
}

function msgrCooldownPlaceholder(seconds) {
    return tf('messenger.input.cooldown', { seconds }, `Cooldown... ${seconds}`);
}

function msgrRateLimitedText(message) {
    return message || t('messenger.cooldown.rate_limited', 'Rate limited');
}

function msgrBoopText(isMine, name) {
    return isMine
        ? tf('messenger.boop.sent', { name }, `You booped ${name}!`)
        : tf('messenger.boop.received', { name }, `${name} booped you!`);
}

function toggleChatPanel() {
    const panel = document.getElementById('chatPanel');
    if (!panel) return;

    const isOpen = panel.classList.contains('panel-open');
    if (isOpen) {
        panel.classList.remove('panel-open');
        setTimeout(() => { if (!panel.classList.contains('panel-open')) panel.style.display = 'none'; }, 90);
        if (_chatPanelDismiss) {
            document.removeEventListener('click', _chatPanelDismiss);
            _chatPanelDismiss = null;
        }
        return;
    }

    panel.style.display = '';
    renderChatPanel();
    requestAnimationFrame(() => panel.classList.add('panel-open'));
    setTimeout(() => {
        _chatPanelDismiss = e => {
            const btn = document.getElementById('btnChat');
            if (!panel.contains(e.target) && !btn?.contains(e.target)) toggleChatPanel();
        };
        document.addEventListener('click', _chatPanelDismiss);
    }, 0);
}

function renderChatPanel() {
    const list = document.getElementById('chatPanelList');
    if (!list) return;

    if (_chatInbox.size === 0) {
        list.innerHTML = `<div class="empty-msg">${esc(t('messages.empty', 'No messages'))}</div>`;
        return;
    }

    list.innerHTML = [..._chatInbox.values()]
        .sort((a, b) => b.time - a.time)
        .map(entry => {
            const avatarStyle = entry.image
                ? `background-image:url('${cssUrl(entry.image)}');background-size:cover;background-position:center;`
                : '';
            const time = entry.time ? msgrFormatTime(entry.time) : '';
            return `<div class="chat-inbox-item" onclick="chatPanelOpen('${esc(entry.userId)}')">
                <div class="chat-inbox-avatar" style="${avatarStyle}"></div>
                <div class="chat-inbox-body">
                    <div class="chat-inbox-row">
                        <span class="chat-inbox-name">${esc(entry.displayName)}</span>
                        <span class="chat-inbox-time">${esc(time)}</span>
                    </div>
                    <div class="chat-inbox-text">${esc(entry.text)}</div>
                </div>
                ${entry.count > 1 ? `<div class="chat-inbox-count">${entry.count}</div>` : ''}
            </div>`;
        }).join('');
}

function chatPanelOpen(userId) {
    toggleChatPanel();
    const entry = _chatInbox.get(userId);
    _chatInbox.delete(userId);
    updateChatBadge();
    openMessenger(userId, entry?.displayName || userId, entry?.image || '', entry?.status || '', entry?.statusDesc || '');
}

function updateChatBadge() {
    const total = [..._chatInbox.values()].reduce((sum, entry) => sum + entry.count, 0);
    const badge = document.getElementById('chatBadge');
    if (!badge) return;
    badge.textContent = total;
    badge.style.display = total > 0 ? '' : 'none';
}

function openMessenger(userId, displayName, image, status, statusDesc) {
    closeMessenger();
    _messengerUserId = userId;
    _messengerName = displayName;
    _messengerImage = image || '';
    _messengerStatus = status || '';
    _messengerStatusDesc = statusDesc || '';

    const statusColor = _msgrStatusColor(status);
    const statusText = msgrStatusText(status, statusDesc);
    const avatarStyle = image
        ? `background-image:url('${cssUrl(image)}');background-size:cover;background-position:center;`
        : '';

    const el = document.createElement('div');
    el.id = 'messengerPanel';
    el.innerHTML = `
        <div id="msgrHeader">
            <div id="msgrHeaderInfo">
                <div id="msgrAvatarWrap">
                    <div id="msgrAvatar" style="${avatarStyle}"></div>
                    <div id="msgrStatusDot" style="background:${statusColor}"></div>
                </div>
                <div id="msgrHeaderText">
                    <div id="msgrName">${esc(displayName)}</div>
                    <div id="msgrSub">${esc(statusText)}</div>
                </div>
            </div>
            <div id="msgrHeaderRight">
                <div id="msgrSlotWrap" title="${esc(msgrSlotTitle())}">
                    <svg id="msgrSlotRing" viewBox="0 0 36 36" width="28" height="28" style="transform:rotate(-90deg)">
                        <circle class="msgr-ring-bg" cx="18" cy="18" r="15.9"></circle>
                        <circle id="msgrRingFg" cx="18" cy="18" r="15.9"
                            style="fill:none;stroke:#2DD48C;stroke-width:3;stroke-linecap:round;stroke-dasharray:0 99.9"></circle>
                    </svg>
                    <div id="msgrSlotText">0/24</div>
                </div>
                <button id="msgrClose" onclick="closeMessenger()" title="${esc(t('common.close', 'Close'))}"><span class="msi">close</span></button>
            </div>
        </div>
        <div id="msgrMessages"></div>
        <div id="msgrCooldownBar" style="display:none;">
            <span class="msi" style="font-size:13px;">schedule</span>
            <span id="msgrCooldownText"></span>
        </div>
        <div id="msgrFooter">
            <div id="msgrInputWrap">
                <textarea id="msgrInput" placeholder="${esc(msgrInputPlaceholder(displayName))}" rows="1" maxlength="${MSGR_MAX_CHARS}"
                    oninput="msgrOnInput(this)"
                    onkeydown="if((event.ctrlKey||event.metaKey)&&event.key==='Enter'){event.preventDefault();messengerSend();}"
                ></textarea>
                <span id="msgrCharCount">${MSGR_MAX_CHARS}</span>
            </div>
            <button id="msgrSendBtn" onclick="messengerSend()" title="${esc(t('messenger.send_title', 'Send message'))}">
                <span class="msi">arrow_upward</span>
            </button>
        </div>`;

    document.body.appendChild(el);
    updateSlotIndicator(_messengerSlots.used, _messengerSlots.total);
    sendToCS({ action: 'vrcGetChatHistory', userId });

    if (Date.now() < _msgrCdEnd) {
        setTimeout(_applyCooldownUI, 0);
    } else {
        setTimeout(() => el.querySelector('#msgrInput')?.focus(), 80);
    }
}

function msgrOnInput(el) {
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 120)}px`;
    const remaining = MSGR_MAX_CHARS - el.value.length;
    const counter = document.getElementById('msgrCharCount');
    if (!counter) return;
    counter.textContent = remaining;
    counter.className = remaining <= 10 ? 'msgr-char-warn' : remaining <= 20 ? 'msgr-char-low' : '';
}

function _msgrStatusColor(status) {
    if (!status) return 'var(--tx3)';
    const s = (typeof STATUS_LIST !== 'undefined') && STATUS_LIST.find(x => x.key === status);
    if (s) return s.color;
    const fallback = { active: '#2DD48C', 'join me': '#42A5F5', 'ask me': '#FFA726', busy: '#EF5350' };
    return fallback[status] || 'var(--tx3)';
}

function closeMessenger() {
    clearTimeout(_messengerCooldown);
    clearInterval(_msgrCdInterval);
    _messengerCooldown = null;
    _msgrCdInterval = null;
    _messengerUserId = null;
    _messengerName = '';
    _messengerImage = '';
    _messengerStatus = '';
    _messengerStatusDesc = '';
    _messengerHistory = [];
    document.getElementById('messengerPanel')?.remove();
}

function messengerSend() {
    const input = document.getElementById('msgrInput');
    const text = input?.value?.trim();
    if (!text || !_messengerUserId || Date.now() < _msgrCdEnd) return;

    input.value = '';
    input.style.height = '';
    msgrOnInput(input);
    input.disabled = true;
    document.getElementById('msgrSendBtn').disabled = true;
    sendToCS({ action: 'vrcSendChatMessage', userId: _messengerUserId, text });
}

function handleChatSlotInfo(info) {
    _messengerSlots = info;
    updateSlotIndicator(info.used, info.total);
}

function updateSlotIndicator(used, total) {
    const ring = document.getElementById('msgrRingFg');
    const text = document.getElementById('msgrSlotText');
    if (!ring || !text) return;

    const circumference = 99.9;
    const pct = total > 0 ? used / total : 0;
    const color = pct >= 0.9 ? '#EF5350' : pct >= 0.6 ? '#FFA726' : '#2DD48C';
    ring.style.stroke = color;
    ring.style.strokeDasharray = `${(pct * circumference).toFixed(2)} ${circumference}`;
    text.textContent = `${used}/${total}`;
    text.style.color = color;
}

function handleChatHistory(payload) {
    if (payload.userId !== _messengerUserId) return;
    _messengerHistory = [...(payload.messages || [])];
    renderMessengerHistory(true);
}

function handleChatMessage(msg) {
    if (document.getElementById('messengerPanel') &&
        (msg.from === _messengerUserId || msg.from === 'me')) {
        if (msg.from !== 'me') playMessageSound();
        _messengerHistory.push({ ...msg });
        appendChatMessage(msg, true);
        return;
    }

    if (msg.from === 'me') return;

    playMessageSound();
    const friend = (typeof vrcFriendsData !== 'undefined') && vrcFriendsData.find(x => x.id === msg.from);
    const existing = _chatInbox.get(msg.from) || {
        userId: msg.from,
        displayName: friend?.displayName || msg.from,
        image: friend?.image || '',
        status: friend?.status || '',
        statusDesc: friend?.statusDescription || '',
        count: 0,
    };
    existing.text = msg.text;
    existing.time = msg.time ? new Date(msg.time).getTime() : Date.now();
    existing.count++;
    _chatInbox.set(msg.from, existing);
    updateChatBadge();
    if (document.getElementById('chatPanel')?.style.display !== 'none') renderChatPanel();
}

function msgrRegisterBoopSent(userId) {
    _pendingBoopUserId = userId;
}

function handleBoopSent() {
    const uid = _pendingBoopUserId;
    _pendingBoopUserId = null;
    if (!document.getElementById('messengerPanel')) return;
    if (!uid || uid === _messengerUserId) {
        const msg = { type: 'boop', from: 'me', time: new Date().toISOString() };
        _messengerHistory.push(msg);
        appendChatMessage(msg, true);
    }
}

function handleBoopReceived(senderUserId, senderUsername) {
    if (!document.getElementById('messengerPanel')) return;
    const idMatch = senderUserId && senderUserId === _messengerUserId;
    const nameMatch = senderUsername && senderUsername === _messengerName;
    if (!idMatch && !nameMatch) return;

    const msg = {
        type: 'boop',
        from: senderUserId || _messengerUserId,
        time: new Date().toISOString(),
    };
    _messengerHistory.push(msg);
    appendChatMessage(msg, true);
}

function renderMessengerHistory(scrollToBottom) {
    const container = document.getElementById('msgrMessages');
    if (!container) return;

    const prevScrollTop = container.scrollTop;
    const shouldStick = scrollToBottom || Math.abs(container.scrollHeight - container.clientHeight - container.scrollTop) < 8;
    container.innerHTML = '';
    _messengerHistory.forEach(msg => appendChatMessage(msg, false));
    if (shouldStick) container.scrollTop = container.scrollHeight;
    else container.scrollTop = prevScrollTop;
}

function appendBoopBubble(isMine, name, isoTime, scroll) {
    const container = document.getElementById('msgrMessages');
    if (!container) return;

    const time = isoTime ? msgrFormatTime(isoTime) : msgrFormatTime(Date.now());
    const text = msgrBoopText(isMine, name);
    const div = document.createElement('div');
    div.className = 'msgr-boop-event';
    div.innerHTML = `<span class="msi msgr-boop-icon">favorite</span><span class="msgr-boop-text">${esc(text)}</span><span class="msgr-boop-time">${esc(time)}</span>`;
    container.appendChild(div);
    if (scroll) container.scrollTop = container.scrollHeight;
}

function appendChatMessage(msg, scroll) {
    if (msg.type === 'boop') {
        appendBoopBubble(msg.from === 'me', _messengerName, msg.time, scroll);
        return;
    }

    const container = document.getElementById('msgrMessages');
    if (!container) return;

    const isMine = msg.from === 'me';
    const time   = msg.time ? msgrFormatTime(msg.time) : '';
    const div    = document.createElement('div');
    div.className = `msgr-msg ${isMine ? 'msgr-mine' : 'msgr-theirs'}`;

    const parsed = msgrParseContentId(msg.text);
    if (parsed) {
        div.innerHTML = msgrBuildContentCard(parsed.id, parsed.prefix, time);
        container.appendChild(div);
        msgrFillContentCard(parsed.id, parsed.prefix, div.querySelector('.vrcn-shared-content-message'));
    } else {
        div.innerHTML = `<div class="msgr-bubble">${esc(msg.text)}</div><div class="msgr-time">${esc(time)}</div>`;
        container.appendChild(div);
    }

    if (scroll) container.scrollTop = container.scrollHeight;
}

function handleChatActionResult(payload) {
    const input = document.getElementById('msgrInput');
    const sendBtn = document.getElementById('msgrSendBtn');

    if (!payload.success) {
        if (input) {
            input.disabled = false;
            input.focus();
        }
        if (sendBtn) sendBtn.disabled = false;

        const bar = document.getElementById('msgrCooldownBar');
        const text = document.getElementById('msgrCooldownText');
        if (bar && text) {
            bar.style.display = 'flex';
            text.textContent = msgrRateLimitedText(payload.message);
            text.dataset.serverMessage = payload.message ? '1' : '';
            clearTimeout(_messengerCooldown);
            _messengerCooldown = setTimeout(() => {
                if (bar) bar.style.display = 'none';
            }, 5000);
        }
        return;
    }

    _startSendCooldown();
}

function _startSendCooldown() {
    _msgrCdEnd = Date.now() + MSGR_SEND_COOLDOWN * 1000;
    _applyCooldownUI();
}

function _applyCooldownUI() {
    if (_msgrCdInterval) clearInterval(_msgrCdInterval);

    const remaining = () => Math.max(0, Math.ceil((_msgrCdEnd - Date.now()) / 1000));

    function tick() {
        const input = document.getElementById('msgrInput');
        const btn = document.getElementById('msgrSendBtn');
        if (!input) {
            clearInterval(_msgrCdInterval);
            _msgrCdInterval = null;
            return;
        }

        const r = remaining();
        if (r <= 0) {
            clearInterval(_msgrCdInterval);
            _msgrCdInterval = null;
            input.disabled = false;
            input.placeholder = msgrInputPlaceholder(_messengerName);
            if (btn) btn.disabled = false;
            input.focus();
        } else {
            input.disabled = true;
            input.placeholder = msgrCooldownPlaceholder(r);
            if (btn) btn.disabled = true;
        }
    }

    tick();
    _msgrCdInterval = setInterval(tick, 1000);
}

function rerenderMessengerTranslations() {
    if (document.getElementById('chatPanel')?.style.display !== 'none') {
        renderChatPanel();
    }

    if (!document.getElementById('messengerPanel') || !_messengerUserId) return;

    const input = document.getElementById('msgrInput');
    const sendBtn = document.getElementById('msgrSendBtn');
    const cooldownBar = document.getElementById('msgrCooldownText');
    const draft = input?.value ?? '';
    const wasDisabled = !!input?.disabled;

    const sub = document.getElementById('msgrSub');
    if (sub) sub.textContent = msgrStatusText(_messengerStatus, _messengerStatusDesc);

    const slotWrap = document.getElementById('msgrSlotWrap');
    if (slotWrap) slotWrap.title = msgrSlotTitle();

    const closeBtn = document.getElementById('msgrClose');
    if (closeBtn) closeBtn.title = t('common.close', 'Close');

    if (sendBtn) sendBtn.title = t('messenger.send_title', 'Send message');

    renderMessengerHistory(false);
    updateSlotIndicator(_messengerSlots.used, _messengerSlots.total);

    if (input) {
        input.value = draft;
        input.placeholder = Date.now() < _msgrCdEnd
            ? msgrCooldownPlaceholder(Math.max(0, Math.ceil((_msgrCdEnd - Date.now()) / 1000)))
            : msgrInputPlaceholder(_messengerName);
        msgrOnInput(input);
        input.disabled = wasDisabled;
    }

    if (cooldownBar && cooldownBar.dataset.serverMessage !== '1' && cooldownBar.textContent) {
        cooldownBar.textContent = msgrRateLimitedText('');
    }
}

document.documentElement.addEventListener('languagechange', rerenderMessengerTranslations);
