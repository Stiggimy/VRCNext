/* Space Flight */

let _sfLastState = null;

function sfConnectBtnHtml() {
    return `<span class="msi" style="font-size:16px;">link</span> ${esc(t('common.connect', 'Connect'))}`;
}

function sfDisconnectBtnHtml() {
    return `<span class="msi" style="font-size:16px;">link_off</span> ${esc(t('common.disconnect', 'Disconnect'))}`;
}

function sfStatusText(state) {
    if (!state?.connected) return state?.error || t('steamvr.status.not_connected', 'Not connected');
    return state.dragging
        ? t('steamvr.status.dragging', 'Dragging...')
        : t('steamvr.status.connected', 'Connected to Space');
}

function sfConnect() {
    if (sfConnected) {
        sendToCS({ action: 'sfDisconnect' });
    } else {
        sendToCS({ action: 'sfConnect' });
        sfSendConfig();
    }
}

function sfReset() {
    sendToCS({ action: 'sfReset' });
}

function sfSendConfig() {
    sendToCS({
        action: 'sfConfig',
        dragMultiplier: parseFloat(document.getElementById('sfMultiplier').value) || 1,
        lockX: document.getElementById('sfLockX').checked,
        lockY: document.getElementById('sfLockY').checked,
        lockZ: document.getElementById('sfLockZ').checked,
        leftHand: document.getElementById('sfLeftHand').checked,
        rightHand: document.getElementById('sfRightHand').checked,
        useGrip: document.getElementById('sfUseGrip').checked
    });
}

let _sfAutoTimer = null;
function sfAutoSave() {
    sfSendConfig();
    clearTimeout(_sfAutoTimer);
    _sfAutoTimer = setTimeout(() => saveSettings(), 600);
}

function handleSfUpdate(data) {
    _sfLastState = { ...data };
    sfConnected = data.connected;
    if (typeof updateDashQuickControls === 'function') updateDashQuickControls();

    const dot = document.getElementById('sfDot');
    const txt = document.getElementById('sfStatusText');
    const btn = document.getElementById('sfConnBtn');
    const badge = document.getElementById('badgeSpace');

    if (data.connected) {
        dot.classList.remove('offline');
        dot.classList.add('online');
        txt.textContent = sfStatusText(data);
        txt.style.color = data.dragging ? 'var(--warn)' : 'var(--ok)';
        btn.innerHTML = sfDisconnectBtnHtml();
        if (badge) {
            badge.classList.remove('offline');
            badge.classList.add('online');
        }
    } else {
        dot.classList.remove('online');
        dot.classList.add('offline');
        txt.textContent = sfStatusText(data);
        txt.style.color = data.error ? 'var(--err)' : 'var(--tx3)';
        btn.innerHTML = sfConnectBtnHtml();
        if (badge) {
            badge.classList.remove('online');
            badge.classList.add('offline');
        }
    }

    document.getElementById('sfOffX').textContent = (data.offsetX ?? 0).toFixed(3);
    document.getElementById('sfOffY').textContent = (data.offsetY ?? 0).toFixed(3);
    document.getElementById('sfOffZ').textContent = (data.offsetZ ?? 0).toFixed(3);

    const lc = document.getElementById('sfCtrlL');
    const rc = document.getElementById('sfCtrlR');
    if (lc) lc.classList.toggle('detected', !!data.leftController);
    if (rc) rc.classList.toggle('detected', !!data.rightController);
}

function rerenderSteamVrTranslations() {
    if (_sfLastState) handleSfUpdate(_sfLastState);
}

document.documentElement.addEventListener('languagechange', rerenderSteamVrTranslations);
