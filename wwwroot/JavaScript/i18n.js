let currentLanguage = 'en';
let i18nBundle = {};

const UI_LANGUAGES = {
    en: { label: 'English', flag: '\uD83C\uDDFA\uD83C\uDDF8', locale: 'en-US' },
    de: { label: 'Deutsch', flag: '\uD83C\uDDE9\uD83C\uDDEA', locale: 'de-DE' },
    es: { label: 'Espa\u00F1ol', flag: '\uD83C\uDDEA\uD83C\uDDF8', locale: 'es-ES' },
    fr: { label: 'Fran\u00E7ais', flag: '\uD83C\uDDEB\uD83C\uDDF7', locale: 'fr-FR' },
    ja: { label: '\u65E5\u672C\u8A9E', flag: '\uD83C\uDDEF\uD83C\uDDF5', locale: 'ja-JP' },
    'zh-cn': { label: '\u7B80\u4F53\u4E2D\u6587', flag: '\uD83C\uDDE8\uD83C\uDDF3', locale: 'zh-CN' },
};

function normalizeUiLanguage(language) {
    const normalized = String(language || '').trim().toLowerCase();
    return UI_LANGUAGES[normalized] ? normalized : 'en';
}

function t(key, fallback = '') {
    return i18nBundle[key] ?? fallback;
}

function tf(key, vars = {}, fallback = '') {
    return String(t(key, fallback)).replace(/\{(\w+)\}/g, (_, name) => {
        return vars[name] ?? `{${name}}`;
    });
}

function getLanguageMeta(language) {
    return UI_LANGUAGES[normalizeUiLanguage(language)];
}

function getLanguageLocale(language = currentLanguage) {
    return getLanguageMeta(language)?.locale || 'en-US';
}

function requestTranslation(language = currentLanguage) {
    sendToCS({ action: 'loadTranslation', language: normalizeUiLanguage(language) });
}

function handleTranslationData(payload) {
    currentLanguage = normalizeUiLanguage(payload?.language || currentLanguage);
    i18nBundle = payload?.translations || {};
    document.documentElement.lang = currentLanguage;
    applyTranslations();
}

function applyTranslations(root = document) {
    root.querySelectorAll('[data-i18n]').forEach(el => {
        const value = t(el.dataset.i18n);
        if (value) el.textContent = value;
    });
    root.querySelectorAll('[data-i18n-placeholder]').forEach(el => {
        const value = t(el.dataset.i18nPlaceholder);
        if (value) el.setAttribute('placeholder', value);
    });
    root.querySelectorAll('[data-i18n-title]').forEach(el => {
        const value = t(el.dataset.i18nTitle);
        if (value) el.setAttribute('title', value);
    });
    renderLanguageChips();
    if (typeof renderThemeChips === 'function') renderThemeChips();
    if (typeof renderSpecialThemeChips === 'function') renderSpecialThemeChips();
    if (typeof renderPlayBtnThemeChips === 'function') renderPlayBtnThemeChips();
    if (typeof renderCursorThemeChips === 'function') renderCursorThemeChips();
    document.querySelectorAll('select').forEach(el => el._vnRefresh && el._vnRefresh());
    if (typeof renderFolders === 'function' && settings?.folders) renderFolders(settings.folders);
    if (typeof renderExtraExe === 'function' && settings?.extraExe) renderExtraExe(settings.extraExe);
    if (typeof renderWebhookCards === 'function') renderWebhookCards(settings?.webhooks || settings?.Webhooks || []);
    if (typeof updateCurrentPageTitle === 'function') updateCurrentPageTitle();
    if (typeof updateClock === 'function') updateClock();
    if (typeof updateFavWorldGroupHeader === 'function') updateFavWorldGroupHeader();
    if (typeof renderFavWorlds === 'function'
        && typeof _favWorldsLoaded !== 'undefined'
        && _favWorldsLoaded
        && typeof favWorldsData !== 'undefined'
        && typeof favWorldGroups !== 'undefined') {
        renderFavWorlds({ worlds: favWorldsData, groups: favWorldGroups });
    }
    if (typeof renderNotifications === 'function' && typeof notifications !== 'undefined') renderNotifications(notifications);
    if (typeof renderCurrentInstance === 'function' && typeof currentInstanceData !== 'undefined' && currentInstanceData) renderCurrentInstance(currentInstanceData);
    if (typeof update2FAMessage === 'function' && document.getElementById('modal2FA')?.style.display !== 'none') update2FAMessage();
    if (typeof renderMyProfileContent === 'function' && document.getElementById('modalMyProfile')?.style.display !== 'none') renderMyProfileContent();
    if (typeof renderVrcFriends === 'function' && typeof vrcFriendsLoaded !== 'undefined' && vrcFriendsLoaded) renderVrcFriends(vrcFriendsData);
    if (typeof currentFriendDetail !== 'undefined' && currentFriendDetail && typeof renderFriendDetail === 'function') renderFriendDetail(currentFriendDetail);
    if (typeof filterFavFriends === 'function' && typeof favFriendsData !== 'undefined') filterFavFriends();
    if (typeof renderModList === 'function' && typeof blockedData !== 'undefined' && blockedData !== null) renderModList('blockedList', blockedData, 'block');
    if (typeof renderModList === 'function' && typeof mutedData !== 'undefined' && mutedData !== null) renderModList('mutedList', mutedData, 'mute');
    if (typeof openStatusModal === 'function' && document.getElementById('modalStatus')?.style.display !== 'none' && currentVrcUser) openStatusModal();
    if (typeof refreshFriendInviteModalTranslations === 'function' && window._inviteModalEl) refreshFriendInviteModalTranslations();
    if (typeof refreshImagePickerTranslations === 'function' && document.getElementById('imagePickerOverlay')) refreshImagePickerTranslations();
    if (typeof renderWorldSearchDetail === 'function'
        && typeof _wdCurrentWorldId !== 'undefined'
        && _wdCurrentWorldId
        && typeof worldInfoCache !== 'undefined'
        && worldInfoCache[_wdCurrentWorldId]
        && document.getElementById('modalDetail')?.style.display !== 'none') {
        renderWorldSearchDetail(worldInfoCache[_wdCurrentWorldId]);
    }
    document.documentElement.dispatchEvent(new CustomEvent('languagechange', { detail: { language: currentLanguage } }));
}

function renderLanguageChips() {
    const el = document.getElementById('languageGrid');
    if (!el) return;
    el.innerHTML = Object.entries(UI_LANGUAGES).map(([key, meta]) =>
        `<button class="theme-chip${currentLanguage === key ? ' active' : ''}" onclick="selectLanguage('${key}')"><span class="theme-flag" aria-hidden="true">${meta.flag}</span>${t(`language.${key}`, meta.label)}</button>`
    ).join('');
}

function selectLanguage(language) {
    const nextLanguage = normalizeUiLanguage(language);
    if (nextLanguage === currentLanguage) return;
    currentLanguage = nextLanguage;
    renderLanguageChips();
    requestTranslation(nextLanguage);
    autoSave();
    const hint = document.getElementById('langRestartHint');
    if (hint) hint.style.display = '';
}
