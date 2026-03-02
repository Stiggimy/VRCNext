/* === Init === */
initAllVnSelects();
renderWebhookCards([{}, {}, {}, {}]);
renderThemeChips();
renderDashboard();
fetchDiscovery();
tryLoadLogo();
tryInitNotifySound();
renderChatboxLines();

/* === Borderless window: drag & double-click maximize === */
const winDrag = document.getElementById('winDrag');
if (winDrag) {
    winDrag.addEventListener('mousedown', e => {
        // Only drag from the topbar background, not buttons/badges
        if (e.target.closest('.win-controls, .btn-notif, .mini-badge, button')) return;
        // Skip SC_MOVE on the 2nd click of a double-click so dblclick event can fire
        if (e.button === 0 && e.detail === 1) sendToCS({ action: 'windowDragStart' });
    });
    winDrag.addEventListener('dblclick', e => {
        if (e.target.closest('.win-controls, .btn-notif, .mini-badge, button')) return;
        sendToCS({ action: 'windowMaximize' });
    });
}

/* === Borderless window: edge resize === */
(function () {
    const B = 6;
    const cursorMap = { n: 'n-resize', s: 's-resize', e: 'e-resize', w: 'w-resize', ne: 'ne-resize', nw: 'nw-resize', se: 'se-resize', sw: 'sw-resize' };
    function getDir(x, y) {
        const w = window.innerWidth, h = window.innerHeight;
        const l = x < B, r = x > w - B, t = y < B, b = y > h - B;
        if (t && l) return 'nw'; if (t && r) return 'ne';
        if (b && l) return 'sw'; if (b && r) return 'se';
        if (l) return 'w'; if (r) return 'e';
        if (t) return 'n'; if (b) return 's';
        return null;
    }
    document.addEventListener('mousemove', e => {
        const dir = getDir(e.clientX, e.clientY);
        document.documentElement.style.cursor = dir ? cursorMap[dir] : '';
    });
    document.addEventListener('mousedown', e => {
        if (e.button !== 0) return;
        const dir = getDir(e.clientX, e.clientY);
        if (dir) { e.preventDefault(); sendToCS({ action: 'windowResizeStart', direction: dir }); }
    });
})();

setInterval(updateClock, 1000);
updateClock();

// Silently pre-load timeline data so friend-detail previews work before Tab 12 is visited
sendToCS({ action: 'getTimeline' });
