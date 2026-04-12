function searchLoadingText() {
    return t('search.loading', 'Loading...');
}

function searchNoResultsText() {
    return t('search.no_results', 'No results found');
}

function searchLoadMoreText() {
    return t('search.load_more', 'Load More');
}

function searchUserStatusText(user) {
    const statusKey = user.status || 'offline';
    const statusText = user.statusDescription
        || (typeof statusLabel === 'function' ? statusLabel(statusKey) : (statusKey || t('status.offline', 'Offline')));
    const friendBadge = user.isFriend
        ? ` | <span style="color:var(--ok)">${esc(t('profiles.badges.friend', 'Friend'))}</span>`
        : '';
    return `<span class="vrc-status-dot ${statusDotClass(statusKey)}" style="width:8px;height:8px;display:inline-block;vertical-align:middle;margin-right:2px;"></span> ${esc(statusText)}${friendBadge}`;
}

function searchGroupMembersText(count) {
    return tf('worlds.groups.members', { count }, `${count} members`);
}

function doSearch(type, loadMore) {
    let query = '';
    let targetEl = '';
    let action = '';
    const sType = type === 'people' ? 'people' : type;

    if (type === 'worlds') {
        query = document.getElementById('searchWorldsInput').value.trim();
        targetEl = 'searchWorldsResults';
        action = 'vrcSearchWorlds';
    } else if (type === 'groups') {
        query = document.getElementById('searchGroupsInput').value.trim();
        targetEl = 'searchGroupsResults';
        action = 'vrcSearchGroups';
    } else if (type === 'people') {
        query = document.getElementById('searchPeopleInput').value.trim();
        targetEl = 'searchPeopleResults';
        action = 'vrcSearchUsers';
    }

    if (!query) return;

    if (!loadMore) {
        searchState[sType] = { query, offset: 0, results: [], hasMore: false };
        document.getElementById(targetEl).innerHTML = sk(type === 'people' ? 'friend' : 'world', type === 'people' ? 5 : 3);
    } else {
        const btn = document.getElementById(targetEl).querySelector('.load-more-btn');
        if (btn) btn.textContent = searchLoadingText();
    }

    sendToCS({ action, query: searchState[sType].query, offset: searchState[sType].offset });
}

function renderSearchResults(type, results, offset, hasMore) {
    let targetEl = '';
    let sType = '';
    if (type === 'worlds') {
        targetEl = 'searchWorldsResults';
        sType = 'worlds';
    } else if (type === 'groups') {
        targetEl = 'searchGroupsResults';
        sType = 'groups';
    } else if (type === 'users') {
        targetEl = 'searchPeopleResults';
        sType = 'people';
    }

    const el = document.getElementById(targetEl);
    if (!el) return;

    const state = searchState[sType];
    if (offset === 0) state.results = results;
    else state.results = state.results.concat(results);
    state.offset = state.results.length;
    state.hasMore = hasMore;

    if (state.results.length === 0) {
        el.innerHTML = `<div class="empty-msg">${esc(searchNoResultsText())}</div>`;
        return;
    }

    let html = '';
    if (type === 'worlds') {
        html = state.results.map(w => renderWorldCard(w)).join('');
    } else if (type === 'groups') {
        html = state.results.map(g => `<div class="vrcn-content-card" onclick="openGroupDetail('${esc(g.id)}')">
            <div class="cc-bg"><img src="${g.bannerUrl||'fallback_cover.png'}" onerror="this.src='fallback_cover.png'" style="position:absolute;inset:0;width:100%;height:100%;object-fit:cover;"></div>
            <div class="cc-scrim"></div>
            <div class="cc-content">
                <div class="cc-name">${esc(g.name)}</div>
                <div class="cc-bottom-row">
                    <div class="cc-meta">${g.iconUrl ? `<div class="cc-group-icon" style="background-image:url('${cssUrl(g.iconUrl)}')"></div>` : ''}<span class="msi" style="font-size:12px;">group</span> ${esc(searchGroupMembersText(g.memberCount))}</div>
                    ${g.shortCode ? `<span style="font-size:10px;color:rgba(255,255,255,.4);">${esc(g.shortCode)}</span>` : ''}
                </div>
            </div>
        </div>`).join('');
    } else if (type === 'users') {
        html = state.results.map(u => `<div class="s-card s-card-h" onclick="openFriendDetail('${esc(u.id)}')">
            <div class="s-card-avatar" style="background-image:url('${cssUrl(u.image)}')"></div>
            <div class="s-card-body"><div class="s-card-title">${esc(u.displayName)}</div><div class="s-card-sub">${searchUserStatusText(u)}</div></div></div>`).join('');
    }

    if (state.hasMore) {
        const searchType = sType === 'people' ? 'people' : sType;
        html += `<div style="grid-column:1/-1;text-align:center;padding:12px;"><button class="vrcn-button load-more-btn" onclick="doSearch('${searchType}',true)" style="padding:8px 24px;"><span class="msi" style="font-size:16px;">expand_more</span> ${esc(searchLoadMoreText())}</button></div>`;
    }

    el.innerHTML = html;
}

function openUserDetail(userId) {
    openFriendDetail(userId);
}

function renderUserDetail(u) {
    renderFriendDetail(u);
}

function rerenderSearchTranslations() {
    if (searchState.worlds.query) {
        renderSearchResults('worlds', searchState.worlds.results, 0, searchState.worlds.hasMore);
    }
    if (searchState.groups.query) {
        renderSearchResults('groups', searchState.groups.results, 0, searchState.groups.hasMore);
    }
    if (searchState.people.query) {
        renderSearchResults('users', searchState.people.results, 0, searchState.people.hasMore);
    }
}

document.documentElement.addEventListener('languagechange', rerenderSearchTranslations);
