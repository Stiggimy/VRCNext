## What's New in 2026.1.3

### WebSocket Migration
- VRCNext now uses VRChat's WebSocket API for the majority of real-time updates instead of polling the REST API
- Friend profiles, locations, online status, and profile changes are now **live** — no manual refresh needed
- REST API is still used on startup to initialize data; WebSocket takes over from there

### Performance & Caching
- Improved image and profile caching for faster load times and a smoother experience
- Merged several cache handlers into the FFC Cacher for reduced overhead
- FFC no longer caches a friend's online status to prevent stale visual states
- "Force FFC All" now runs across **4 parallel tasks** for significantly faster execution

### Timeline
- Timeline defaults to **Today** on first open — no more empty view on startup
- New **Date Picker** to filter timeline entries by a specific day; press Clear to remove the filter
- Timeline now logs when a friend **changes their status text**
- **Paginated loading** — timeline entries load dynamically to keep memory usage low even with hundreds of thousands of records

### People Tab
- Switching between Favorites, Blocked, and Muted sub-tabs now **automatically refreshes** the list from VRChat's API
- Added a **Refresh button** to manually force a refresh of the current sub-tab at any time

### UI Redesign
- Avatars Tab: redesigned filter buttons and dropdowns to match the app theme
- Worlds Tab: redesigned filter buttons and dropdowns to match the app theme
- Groups Tab: redesigned buttons and layout to match the app theme
- Timeline Tab: redesigned filter buttons and layout to match the app theme
