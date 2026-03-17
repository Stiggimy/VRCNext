/* === VR Wrist Overlay === */

let vroConnected    = false;
let vroVisible      = false;
let vroRecording    = false;
let vroKeybindMode  = 0;   // 0=combo(hold), 1=doubletap — which mode is ACTIVE

// Combo slot (1–4 buttons held)
let vroComboIds    = [];
let vroComboHand   = 0;   // 0=any, 1=left, 2=right

// Double-tap slot (exactly 1 button, double-press)
let vroDtIds       = [];
let vroDtHand      = 0;   // 0=any, 1=left, 2=right

let _vroAutoTimer   = null;
let _vroPrevHand    = null; // tracks previous hand selection for mirror logic

// Button name lookup (mirrors C# ButtonNames dictionary)
const VRO_BTN_NAMES = { 1: 'B/Y', 2: 'Grip', 7: 'A/X', 32: 'Stick', 33: 'Trigger' };
function vroGetNames(ids) {
    return ids.map(id => VRO_BTN_NAMES[id] ?? `Button${id}`);
}

// ── Helpers to get/set active slot ───────────────────────────────────────────

function vroActiveIds()  { return vroKeybindMode === 0 ? vroComboIds  : vroDtIds;  }
function vroActiveHand() { return vroKeybindMode === 0 ? vroComboHand : vroDtHand; }

// ── State sync from C# ───────────────────────────────────────────────────────

function handleVroState(d) {
    vroConnected    = !!d.connected;
    vroVisible      = !!d.visible;
    vroRecording    = !!d.recording;

    if (d.keybind     !== undefined) vroComboIds   = d.keybind     || [];
    if (d.keybindHand !== undefined) vroComboHand  = d.keybindHand ?? 0;
    if (d.keybindDt   !== undefined) vroDtIds      = d.keybindDt   || [];
    if (d.keybindDtHand !== undefined) vroDtHand   = d.keybindDtHand ?? 0;
    if (d.keybindMode !== undefined) vroKeybindMode = d.keybindMode ?? 0;

    const dot   = document.getElementById('vroDot');
    const txt   = document.getElementById('vroStatusText');
    const btn   = document.getElementById('vroConnBtn');
    const badge = document.getElementById('badgeVro');

    if (d.connected) {
        dot?.classList.replace('offline', 'online');
        if (txt) txt.textContent = 'Connected';
        if (txt) txt.style.color = 'var(--ok)';
        if (btn) btn.innerHTML = '<span class="msi" style="font-size:16px;">link_off</span> Disconnect';
        badge?.classList.replace('offline', 'online');
        if (currentSpecialTheme === 'auto') applyAutoColor();
        else {
            const t = (typeof THEMES !== 'undefined' && THEMES[currentTheme])
                   || (typeof customThemes !== 'undefined' && customThemes.find(x => x.key === currentTheme));
            if (t) applyColors(t.c);
        }
    } else {
        dot?.classList.replace('online', 'offline');
        if (txt) txt.textContent = d.error || 'Not connected';
        if (txt) txt.style.color = d.error ? 'var(--err)' : 'var(--tx3)';
        if (btn) btn.innerHTML = '<span class="msi" style="font-size:16px;">link</span> Connect';
        badge?.classList.replace('online', 'offline');
    }

    const controlCard = document.getElementById('vroControlCard');
    if (controlCard) controlCard.style.display = d.connected ? '' : 'none';

    const showBtn = document.getElementById('vroShowBtn');
    const hideBtn = document.getElementById('vroHideBtn');
    if (showBtn) { showBtn.disabled = !d.connected; showBtn.style.opacity = d.connected ? '1' : '0.4'; }
    if (hideBtn) { hideBtn.disabled = !d.connected; hideBtn.style.opacity = d.connected ? '1' : '0.4'; }

    const visIco = document.getElementById('vroVisIcon');
    const visTxt = document.getElementById('vroVisText');
    if (visIco) visIco.textContent = d.visible ? 'visibility_off' : 'visibility';
    if (visTxt) visTxt.textContent = d.visible ? 'Hide Overlay' : 'Show Overlay';

    updateModePill();
    updateKeybindDisplay();
    updateRecordingUI();

    const leftEl  = document.getElementById('vroCtrlL');
    const rightEl = document.getElementById('vroCtrlR');
    if (leftEl)  leftEl.classList.toggle('detected', !!d.leftController);
    if (rightEl) rightEl.classList.toggle('detected', !!d.rightController);
}

function handleVroKeybindRecorded(d) {
    const ids  = d.ids  || [];
    const hand = d.hand ?? 0;
    const mode = d.mode ?? 0;

    if (mode === 0) { vroComboIds = ids; vroComboHand = hand; }
    else            { vroDtIds    = ids; vroDtHand    = hand; }

    // Switch active mode to match what was just recorded
    vroKeybindMode = mode;
    vroRecording   = false;
    updateModePill();
    updateKeybindDisplay();
    updateRecordingUI();
    vroSendConfig();
}

// ── Connect / disconnect ──────────────────────────────────────────────────────

function vroConnect() {
    if (vroConnected) {
        sendToCS({ action: 'vroDisconnect' });
    } else {
        // Resolve the current theme colors and send them with the connect
        // message so C# can seed the overlay immediately — no round-trip needed.
        let colors = null;
        if (currentSpecialTheme === 'auto') {
            // Grab whatever CSS vars are currently active (auto color already applied)
            const s = getComputedStyle(document.documentElement);
            const keys = ['bg-card','bg-hover','accent','ok','warn','err','cyan','tx1','tx2','tx3','brd'];
            colors = {};
            for (const k of keys) {
                const v = s.getPropertyValue('--' + k).trim();
                if (v) colors[k] = v;
            }
        } else {
            const t = (typeof THEMES !== 'undefined' && THEMES[currentTheme])
                   || (typeof customThemes !== 'undefined' && customThemes.find(x => x.key === currentTheme));
            if (t) colors = t.c;
        }
        sendToCS({ action: 'vroConnect', themeColors: colors || null });
        vroSendConfig();
    }
}

// ── Show / hide overlay ───────────────────────────────────────────────────────

function vroToggleVisibility() {
    if (!vroConnected) return;
    sendToCS({ action: vroVisible ? 'vroHide' : 'vroShow' });
}

// ── Config ────────────────────────────────────────────────────────────────────

function vroSendConfig() {
    const attachLeft = document.getElementById('vroAttachLeft')?.value === 'left';

    sendToCS({
        action:        'vroConfig',
        attachLeft,
        attachHand:    true,
        posX:          parseFloat(document.getElementById('vroPosX')?.value)  || -0.10,
        posY:          parseFloat(document.getElementById('vroPosY')?.value)  || -0.03,
        posZ:          parseFloat(document.getElementById('vroPosZ')?.value)  || 0.11,
        rotX:          parseFloat(document.getElementById('vroRotX')?.value)  || -180,
        rotY:          parseFloat(document.getElementById('vroRotY')?.value)  || 46,
        rotZ:          parseFloat(document.getElementById('vroRotZ')?.value)  || 85,
        width:         parseFloat(document.getElementById('vroWidth')?.value) || 0.16,
        keybind:        vroComboIds,
        keybindHand:    vroComboHand,
        keybindDt:      vroDtIds,
        keybindDtHand:  vroDtHand,
        keybindMode:    vroKeybindMode,
        controlRadius:  parseFloat(document.getElementById('vroControlRadius')?.value) || 28,
    });
}

function vroAutoSave() {
    vroSendConfig();
    clearTimeout(_vroAutoTimer);
    _vroAutoTimer = setTimeout(() => saveSettings(), 600);
}

// Called when the Hand dropdown changes — mirrors transform then saves
function vroMirrorAndSave() {
    const handEl = document.getElementById('vroAttachLeft');
    const isLeft = handEl?.value === 'left';

    if (_vroPrevHand !== null && _vroPrevHand !== isLeft) {
        const posX = document.getElementById('vroPosX');
        const rotY = document.getElementById('vroRotY');
        const rotZ = document.getElementById('vroRotZ');
        if (posX) { posX.value = String(-parseFloat(posX.value)); vroUpdateTransformLabel('vroPosX'); }
        if (rotY) { rotY.value = String(-parseFloat(rotY.value)); vroUpdateTransformLabel('vroRotY'); }
        if (rotZ) { rotZ.value = String(-parseFloat(rotZ.value)); vroUpdateTransformLabel('vroRotZ'); }
    }
    _vroPrevHand = isLeft;
    vroAutoSave();
}

function vroAutoSaveSettings() {
    sendToCS({
        action: 'vroAutoSave',
        autoStart:   false, // legacy
        autoStartVR: !!document.getElementById('setVroAutoStartVR')?.checked,
    });
    clearTimeout(_vroAutoTimer);
    _vroAutoTimer = setTimeout(() => saveSettings(), 600);
}

// ── Mode pill ─────────────────────────────────────────────────────────────────

function vroSetMode(mode) {
    if (mode === vroKeybindMode) return; // already active — don't clear anything
    vroKeybindMode = mode;
    updateModePill();
    updateKeybindDisplay();
    updateRecordingUI();
    vroSendConfig();
}

function updateModePill() {
    const comboBtn  = document.getElementById('vroModeCombo');
    const dtBtn     = document.getElementById('vroModeDoubleTap');
    if (!comboBtn || !dtBtn) return;
    comboBtn.classList.toggle('active', vroKeybindMode === 0);
    dtBtn.classList.toggle('active',    vroKeybindMode === 1);
}

// ── Keybind recording ─────────────────────────────────────────────────────────

function vroStartRecording() {
    if (!vroConnected) return;
    vroRecording = true;
    updateRecordingUI();
    sendToCS({ action: 'vroRecordKeybind' });
}

function vroCancelRecording() {
    vroRecording = false;
    updateRecordingUI();
    sendToCS({ action: 'vroCancelRecording' });
}

function vroClearKeybind() {
    if (vroKeybindMode === 0) { vroComboIds = []; vroComboHand = 0; }
    else                      { vroDtIds    = []; vroDtHand    = 0; }
    updateKeybindDisplay();
    vroSendConfig();
}

function updateRecordingUI() {
    const recordBtn = document.getElementById('vroRecordBtn');
    const cancelBtn = document.getElementById('vroCancelRecordBtn');
    const hint      = document.getElementById('vroRecordHint');

    if (vroRecording) {
        if (recordBtn) recordBtn.style.display = 'none';
        if (cancelBtn) cancelBtn.style.display = 'flex';
        if (hint) {
            hint.textContent = vroKeybindMode === 1
                ? 'Press one button and hold to record for Double Tap…'
                : 'Hold 1–4 buttons simultaneously to record a combo…';
            hint.style.color = 'var(--warn)';
        }
    } else {
        if (recordBtn) recordBtn.style.display = 'flex';
        if (cancelBtn) cancelBtn.style.display = 'none';
        if (hint) {
            hint.textContent = vroKeybindMode === 1
                ? 'Double-tap a single button to toggle the overlay.'
                : 'Hold 1–4 buttons on one controller to toggle the overlay.';
            hint.style.color = 'var(--tx3)';
        }
    }
}

function updateKeybindDisplay() {
    const display = document.getElementById('vroKeybindDisplay');
    const visual  = document.getElementById('vroControllerVisual');

    const ids  = vroActiveIds();
    const hand = vroActiveHand();

    if (!display) return;

    if (ids.length === 0) {
        display.innerHTML = '<span style="color:var(--tx3);font-style:italic;">No keybind set</span>';
    } else {
        const names = vroGetNames(ids);
        const sideLabel = hand === 1 ? '<span class="vro-keybind-chip" style="background:var(--accent20);color:var(--accent);">L</span>'
                        : hand === 2 ? '<span class="vro-keybind-chip" style="background:var(--accent20);color:var(--accent);">R</span>'
                        : '';
        const modeChip = vroKeybindMode === 1
            ? '<span class="vro-keybind-chip" style="background:color-mix(in srgb,var(--cyan) 20%,transparent);color:var(--cyan);border-color:var(--cyan);">×2</span>'
            : '';
        const chips = names
            .map(n => `<span class="vro-keybind-chip">${n}</span>`)
            .join('<span class="vro-keybind-plus">+</span>');
        const sep = (sideLabel || modeChip) ? '<span class="vro-keybind-plus">·</span>' : '';
        display.innerHTML = modeChip + sideLabel + sep + chips;
    }

    if (!visual) return;
    visual.querySelectorAll('.vro-btn').forEach(el => {
        el.classList.remove('active');
        const btnId = parseInt(el.dataset.btnId ?? '999');
        if (ids.includes(btnId)) el.classList.add('active');
    });
}

// ── Transform value display ───────────────────────────────────────────────────

function vroUpdateTransformLabel(id) {
    const input = document.getElementById(id);
    const label = document.getElementById(id + 'Val');
    if (!input || !label) return;
    label.textContent = parseFloat(input.value).toFixed(2);
}

function vroUpdateControlRadius() {
    const input = document.getElementById('vroControlRadius');
    const label = document.getElementById('vroControlRadiusVal');
    if (input && label) label.textContent = input.value + ' cm';
    vroAutoSave();
}

function vroResetTransform() {
    const defaults = {
        vroPosX: -0.10, vroPosY: -0.03, vroPosZ: 0.11,
        vroRotX: -180,  vroRotY:  46,   vroRotZ: 85,
        vroWidth: 0.16
    };
    for (const [id, val] of Object.entries(defaults)) {
        const el = document.getElementById(id);
        if (el) { el.value = val; vroUpdateTransformLabel(id); }
    }
    const crEl  = document.getElementById('vroControlRadius');
    const crLbl = document.getElementById('vroControlRadiusVal');
    if (crEl)  crEl.value = 16;
    if (crLbl) crLbl.textContent = '16 cm';
    vroAutoSave();
}

// ── Load settings from C# ─────────────────────────────────────────────────────

function vroLoadSettings(s) {
    if (!s) return;

    const attachLeftEl = document.getElementById('vroAttachLeft');
    if (attachLeftEl) attachLeftEl.value = s.vroAttachLeft ? 'left' : 'right';

    const ids  = ['vroPosX','vroPosY','vroPosZ','vroRotX','vroRotY','vroRotZ','vroWidth'];
    const keys = ['vroPosX','vroPosY','vroPosZ','vroRotX','vroRotY','vroRotZ','vroWidth'];
    ids.forEach((id, i) => {
        const el = document.getElementById(id);
        if (el && s[keys[i]] !== undefined) {
            el.value = s[keys[i]];
            vroUpdateTransformLabel(id);
        }
    });

    const autoVrEl = document.getElementById('setVroAutoStartVR');
    if (autoVrEl) autoVrEl.checked = !!(s.vroAutoStartVR ?? s.autoStartVR ?? false);

    vroComboIds   = s.vroKeybind   || [];
    vroComboHand  = s.vroKeybindHand ?? 0;
    vroDtIds      = s.vroKeybindDt   || [];
    vroDtHand     = s.vroKeybindDtHand ?? 0;
    vroKeybindMode = s.vroKeybindMode ?? 0;

    const crEl = document.getElementById('vroControlRadius');
    const crLbl = document.getElementById('vroControlRadiusVal');
    const crVal = s.vroControlRadius ?? 28;
    if (crEl)  crEl.value = crVal;
    if (crLbl) crLbl.textContent = crVal + ' cm';

    updateModePill();
    updateKeybindDisplay();
    updateRecordingUI();

    _vroPrevHand = s.vroAttachLeft ? true : false;

    // Toast notification settings
    const _toastEls = {
        vroToastEnabled:    s.vroToastEnabled    ?? true,
        vroToastFavOnly:    s.vroToastFavOnly    ?? false,
        vroToastOnline:     s.vroToastOnline     ?? true,
        vroToastOffline:    s.vroToastOffline    ?? true,
        vroToastGps:        s.vroToastGps        ?? true,
        vroToastStatus:     s.vroToastStatus     ?? true,
        vroToastStatusDesc: s.vroToastStatusDesc ?? true,
        vroToastBio:        s.vroToastBio        ?? true,
    };
    for (const [id, val] of Object.entries(_toastEls)) {
        const el = document.getElementById(id);
        if (el) el.checked = val;
    }
    const tSizeEl = document.getElementById('vroToastSize');
    if (tSizeEl) { tSizeEl.value = s.vroToastSize ?? 50; vroToastUpdateLabel('vroToastSize'); }
    const tOffXEl = document.getElementById('vroToastOffsetX');
    if (tOffXEl) { tOffXEl.value = s.vroToastOffsetX ?? 0; vroToastUpdateLabel('vroToastOffsetX'); }
    const tOffYEl = document.getElementById('vroToastOffsetY');
    if (tOffYEl) { tOffYEl.value = s.vroToastOffsetY ?? -0.12; vroToastUpdateLabel('vroToastOffsetY'); }
}

// ── Toast notification settings ──────────────────────────────────────────────

let _vroToastTimer = null;

function vroToastUpdateLabel(id) {
    const input = document.getElementById(id);
    const label = document.getElementById(id + 'Val');
    if (!input || !label) return;
    if (id === 'vroToastSize') label.textContent = input.value + '%';
    else label.textContent = parseFloat(input.value).toFixed(2);
}

function vroToastAutoSave() {
    vroToastSendConfig();
    clearTimeout(_vroToastTimer);
    _vroToastTimer = setTimeout(() => saveSettings(), 600);
}

function vroToastSendConfig() {
    sendToCS({
        action:      'vroToastConfig',
        enabled:     !!document.getElementById('vroToastEnabled')?.checked,
        favOnly:     !!document.getElementById('vroToastFavOnly')?.checked,
        size:        parseInt(document.getElementById('vroToastSize')?.value) || 50,
        offsetX:     parseFloat(document.getElementById('vroToastOffsetX')?.value) || 0,
        offsetY:     parseFloat(document.getElementById('vroToastOffsetY')?.value) || -0.12,
        online:      !!document.getElementById('vroToastOnline')?.checked,
        offline:     !!document.getElementById('vroToastOffline')?.checked,
        gps:         !!document.getElementById('vroToastGps')?.checked,
        status:      !!document.getElementById('vroToastStatus')?.checked,
        statusDesc:  !!document.getElementById('vroToastStatusDesc')?.checked,
        bio:         !!document.getElementById('vroToastBio')?.checked,
    });
}
