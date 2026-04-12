/* === Calendar Tab === */

let calendarLoaded = false;
let calendarFilter = 'all';
let _calEvents = [];
let _calSelectedDay = null;
let _calYear = new Date().getFullYear();
let _calMonth = new Date().getMonth();
let _calLoading = false;
var _calDashPending = 0;
var _calDashRawEvents = [];

(function _calCSS() {
    if (document.getElementById('cal-css')) return;
    const s = document.createElement('style');
    s.id = 'cal-css';
    s.textContent = `
        .cal-day {
            background: var(--bg-card);
            border: 1px solid rgba(255,255,255,.06);
            border-radius: 8px;
            min-height: 100px;
            padding: 8px 6px 6px;
            cursor: pointer;
            transition: background .12s, border-color .12s;
            overflow: hidden;
            box-sizing: border-box;
        }
        .cal-day:hover { background: var(--bg-hover); border-color: color-mix(in srgb,var(--accent) 40%,transparent); }
        .cal-day.cal-today { border-color: color-mix(in srgb,var(--accent) 65%,transparent); background: color-mix(in srgb,var(--accent) 7%,transparent); }
        .cal-day.cal-sel { border: 2px solid var(--accent); background: color-mix(in srgb,var(--accent) 14%,transparent); }
        .cal-day.cal-empty { background: rgba(255,255,255,.015); cursor: default; pointer-events: none; }
        .cal-day-num { font-size: 12px; font-weight: 600; color: var(--tx2); margin-bottom: 4px; line-height: 1; }
        .cal-day.cal-today .cal-day-num,
        .cal-day.cal-sel .cal-day-num { color: var(--accent-lt); }
        .cal-chip {
            display: block;
            font-size: 9.5px;
            padding: 2px 5px;
            border-radius: 4px;
            margin-bottom: 2px;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
            line-height: 1.4;
            cursor: pointer;
        }
        .cal-chip:hover { opacity: .8; }
        .cal-chip-f { background: rgba(245,158,11,.2); color: #f6c265; }
        .cal-chip-g { background: color-mix(in srgb,var(--accent) 20%,transparent); color: var(--accent-lt); }
        .cal-chip-more { display: block; font-size: 9px; color: var(--tx3); padding: 1px 2px; }
        .cal-day-hdr { text-align: center; font-size: 10px; font-weight: 700; color: var(--tx3); padding: 4px 2px 6px; letter-spacing: .6px; text-transform: uppercase; }
        .cal-evlist-card {
            display: flex;
            gap: 10px;
            align-items: flex-start;
            padding: 10px 12px;
            background: var(--bg-card);
            border-radius: 10px;
            margin-bottom: 8px;
            cursor: pointer;
            border: 1px solid transparent;
            transition: border-color .12s, background .12s;
        }
        .cal-evlist-card:hover { border-color: color-mix(in srgb,var(--accent) 40%,transparent); background: var(--bg-hover); }
        .cal-evlist-thumb {
            width: 56px;
            height: 56px;
            border-radius: 7px;
            object-fit: cover;
            flex-shrink: 0;
            background: var(--bg-hover);
            display: flex;
            align-items: center;
            justify-content: center;
        }
        .cal-day-panel {
            margin-top: 14px;
            padding: 14px 0 0;
            border-top: 1px solid rgba(255,255,255,.07);
        }
        .cal-day-panel-hdr {
            font-size: 13px;
            font-weight: 700;
            color: var(--tx1);
            margin-bottom: 10px;
            display: flex;
            align-items: center;
            gap: 6px;
        }
    `;
    document.head.appendChild(s);
}());

function _calDateLocale() {
    return getLanguageLocale();
}

function _renderCalUI() {
    const tab = document.getElementById('tab17');
    if (!tab) return;

    const refreshTitle = esc(t('calendar.refresh_title', 'Refresh calendar'));
    const refreshIcon = _calLoading ? 'hourglass_empty' : 'refresh';
    const refreshDisabled = _calLoading ? ' disabled' : '';

    tab.innerHTML = `<div id="calInner">
        <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:16px;gap:8px;">
            <div style="display:flex;align-items:center;gap:4px;">
                <button class="vrcn-button" onclick="_calNavMonth(-1)"><span class="msi" style="font-size:18px;">chevron_left</span></button>
                <span id="calMonthLabel" style="min-width:140px;text-align:center;font-size:14px;font-weight:700;color:var(--tx0);"></span>
                <button class="vrcn-button" onclick="_calNavMonth(1)"><span class="msi" style="font-size:18px;">chevron_right</span></button>
                <button class="vrcn-button sub-tab-btn cal-filter-btn${calendarFilter === 'all' ? ' active' : ''}" data-filter="all" onclick="setCalFilter('all')"><span class="msi" style="font-size:14px;">calendar_month</span> ${esc(t('calendar.filters.all', 'All'))}</button>
                <button class="vrcn-button sub-tab-btn cal-filter-btn${calendarFilter === 'featured' ? ' active' : ''}" data-filter="featured" onclick="setCalFilter('featured')"><span class="msi" style="font-size:14px;">star</span> ${esc(t('calendar.filters.featured', 'Featured'))}</button>
                <button class="vrcn-button sub-tab-btn cal-filter-btn${calendarFilter === 'following' ? ' active' : ''}" data-filter="following" onclick="setCalFilter('following')"><span class="msi" style="font-size:14px;">notifications_active</span> ${esc(t('calendar.filters.following', 'Following'))}</button>
                <button class="vrcn-button" id="calRefreshBtn" onclick="refreshCalendar()" title="${refreshTitle}"${refreshDisabled}><span class="msi" style="font-size:18px;">${refreshIcon}</span></button>
            </div>
        </div>
        <div id="calGridArea"></div>
        <div id="calDayPanel" style="display:none;"></div>
    </div>`;

    _updateMonthLabel();
}

function _syncCalView() {
    if (!document.getElementById('tab17')) return;
    _renderCalUI();
    if (_calLoading) {
        const gridArea = document.getElementById('calGridArea');
        if (gridArea) {
            gridArea.innerHTML = `<div class="empty-msg" style="padding:40px 0;">${esc(t('calendar.loading', 'Loading events...'))}</div>`;
        }
        return;
    }
    _buildGrid();
    const dayEvents = _calSelectedDay ? _eventsForDay(_calSelectedDay) : [];
    _buildDayPanel(dayEvents, _calSelectedDay);
}

function _initCalUI() {
    const tab = document.getElementById('tab17');
    if (!tab || document.getElementById('calInner')) return;
    _renderCalUI();
}

function _updateMonthLabel() {
    const el = document.getElementById('calMonthLabel');
    if (!el) return;
    el.textContent = new Date(_calYear, _calMonth, 1).toLocaleDateString(_calDateLocale(), { month: 'long', year: 'numeric' });
}

function refreshCalendar() {
    _initCalUI();
    _calLoading = true;
    _syncCalView();
    sendToCS({ action: 'vrcGetCalendarEvents', filter: calendarFilter, year: _calYear, month: _calMonth + 1 });
}

function setCalFilter(filter) {
    if (calendarFilter === filter) return;
    calendarFilter = filter;
    _calEvents = [];
    _calSelectedDay = null;
    refreshCalendar();
}

function renderCalendarEvents(payload) {
    let raw = payload;
    if (raw?.events) raw = raw.events;
    else if (raw?.results) raw = raw.results;
    else if (raw?.data) raw = raw.data;
    let all = Array.isArray(raw) ? raw : [];

    // Dashboard-only fetch: accumulate but don't touch calendar state or UI
    if (_calDashPending > 0) {
        _calDashRawEvents = _calDashRawEvents.concat(all);
        _calDashPending--;
        if (_calDashPending <= 0 && typeof onCalendarEventsForDash === 'function') {
            onCalendarEventsForDash(_calDashRawEvents);
        }
        return;
    }

    // Normal calendar flow
    calendarLoaded = true;
    _calLoading = false;

    if (calendarFilter === 'featured') {
        all = all.filter(e => e.featured === true || _isFeatured(e));
    }

    _calEvents = all;
    _calSelectedDay = null;
    _syncCalView();
    if (typeof onCalendarEventsForDash === 'function') onCalendarEventsForDash(_calEvents);
}

function _calNavMonth(delta) {
    _calMonth += delta;
    if (_calMonth > 11) {
        _calMonth = 0;
        _calYear++;
    }
    if (_calMonth < 0) {
        _calMonth = 11;
        _calYear--;
    }
    _calSelectedDay = null;
    _calEvents = [];
    _calLoading = true;
    _syncCalView();
    sendToCS({ action: 'vrcGetCalendarEvents', filter: calendarFilter, year: _calYear, month: _calMonth + 1 });
}

function _calClickDay(key) {
    _calSelectedDay = _calSelectedDay === key ? null : key;
    _buildGrid();
    const dayEvents = _calSelectedDay ? _eventsForDay(_calSelectedDay) : [];
    _buildDayPanel(dayEvents, _calSelectedDay);
}

function _eventKey(evt) {
    const date = new Date(evt.startsAt || evt.startDate || '');
    if (isNaN(date)) return null;
    return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, '0')}-${String(date.getUTCDate()).padStart(2, '0')}`;
}

function _eventsForDay(key) {
    return _calEvents.filter(evt => _eventKey(evt) === key);
}

function _isFeatured(evt) {
    return Array.isArray(evt.tags) && evt.tags.some(tag => /featured/i.test(tag));
}

function _buildGrid() {
    const wrap = document.getElementById('calGridArea');
    if (!wrap) return;

    const dayMap = {};
    _calEvents.forEach(evt => {
        const key = _eventKey(evt);
        if (key) (dayMap[key] = dayMap[key] || []).push(evt);
    });

    const today = new Date();
    const todayKey = `${today.getUTCFullYear()}-${String(today.getUTCMonth() + 1).padStart(2, '0')}-${String(today.getUTCDate()).padStart(2, '0')}`;
    const firstDay = new Date(_calYear, _calMonth, 1).getDay();
    const daysInMonth = new Date(_calYear, _calMonth + 1, 0).getDate();

    const hdr = Array.from({ length: 7 }, (_, idx) => {
        const label = new Date(Date.UTC(2024, 0, 7 + idx)).toLocaleDateString(_calDateLocale(), { weekday: 'short' });
        return `<div class="cal-day-hdr">${esc(label.toUpperCase())}</div>`;
    }).join('');

    let cells = '';
    for (let i = 0; i < firstDay; i++) {
        cells += '<div class="cal-day cal-empty"></div>';
    }

    for (let day = 1; day <= daysInMonth; day++) {
        const key = `${_calYear}-${String(_calMonth + 1).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
        const events = dayMap[key] || [];
        const isToday = key === todayKey;
        const isSelected = key === _calSelectedDay;
        let cls = 'cal-day';
        if (isToday) cls += ' cal-today';
        if (isSelected) cls += ' cal-sel';

        const chips = events.slice(0, 3).map(evt => {
            const groupId = esc(evt.ownerId || '');
            const eventId = esc(evt.id || '');
            const chipCls = _isFeatured(evt) ? 'cal-chip-f' : 'cal-chip-g';
            const title = evt.title || t('calendar.event_fallback', 'Event');
            return `<span class="cal-chip ${chipCls}" onclick="event.stopPropagation();openEventDetail('${groupId}','${eventId}')" title="${esc(title)}">${esc(title)}</span>`;
        }).join('');

        const moreCount = events.length - 3;
        const moreFallback = `+${moreCount} more`;
        const more = events.length > 3
            ? `<span class="cal-chip-more">${esc(tf('calendar.more_count', { count: moreCount }, moreFallback))}</span>`
            : '';

        cells += `<div class="${cls}" onclick="_calClickDay('${key}')">
            <div class="cal-day-num">${day}</div>${chips}${more}
        </div>`;
    }

    wrap.innerHTML = `<div style="display:grid;grid-template-columns:repeat(7,1fr);gap:5px;">${hdr}${cells}</div>`;
}

function _buildDayPanel(events, key) {
    const el = document.getElementById('calDayPanel');
    if (!el) return;

    if (!key || events.length === 0) {
        el.style.display = 'none';
        return;
    }

    const dayLabel = fmtLongDate(new Date(`${key}T12:00:00Z`));

    const cards = events
        .sort((a, b) => new Date(a.startsAt || a.startDate || 0) - new Date(b.startsAt || b.startDate || 0))
        .map(evt => {
            const date = new Date(evt.startsAt || evt.startDate || '');
            const timeStr = !isNaN(date) ? fmtTime(date) : '';
            const tags = Array.isArray(evt.tags) ? evt.tags : [];
            const tagHtml = tags.slice(0, 4).map(tag => {
                const featured = /featured/i.test(tag);
                return `<span class="vrcn-badge${featured ? ' warn' : ''}">${esc(tag)}</span>`;
            }).join('');
            const imgHtml = evt.imageUrl
                ? `<img class="cal-evlist-thumb" src="${evt.imageUrl}" onerror="this.style.display='none'">`
                : `<div class="cal-evlist-thumb"><span class="msi" style="font-size:22px;color:var(--tx3);">event</span></div>`;

            return `<div class="cal-evlist-card" onclick="openEventDetail('${esc(evt.ownerId || '')}','${esc(evt.id || '')}')">
                ${imgHtml}
                <div style="flex:1;min-width:0;">
                    <div style="font-size:12px;font-weight:600;color:var(--tx0);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-bottom:3px;">${esc(evt.title || t('calendar.untitled_event', 'Untitled Event'))}</div>
                    ${timeStr ? `<div style="font-size:10px;color:var(--tx2);margin-bottom:4px;">${esc(timeStr)}</div>` : ''}
                    <div style="display:flex;flex-wrap:wrap;gap:3px;">${tagHtml}</div>
                </div>
            </div>`;
        }).join('');

    el.innerHTML = `<div class="cal-day-panel">
        <div class="cal-day-panel-hdr">
            <span class="msi" style="font-size:16px;color:var(--accent-lt);">calendar_today</span>${esc(dayLabel)}
            <button class="vrcn-button" onclick="_calClickDay('${key}')" style="margin-left:auto;padding:2px 8px;font-size:11px;" title="${esc(t('common.close', 'Close'))}"><span class="msi" style="font-size:14px;">close</span></button>
        </div>
        <div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:8px;">${cards}</div>
    </div>`;
    el.style.display = 'block';
}

function rerenderCalendarTranslations() {
    if (!document.getElementById('calInner')) return;
    _syncCalView();
}

document.documentElement.addEventListener('languagechange', rerenderCalendarTranslations);
