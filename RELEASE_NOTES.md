**2026.14.0**

**Performance**
- Reduced Image quality of the fallback banner which means slightly less memory usage.

**Setup Wizard**

* Redesigned titlebar buttons to macOS-style colored circles
* Added Language selection as the first step of the Setup Wizard
* The Setup Wizard now translates immediately when a language is selected
* Fixed: selected language is now correctly saved and applied to the launcher after setup completes

**Bug Fixes**

* Fixed a fatal crash (`0x80131506`) caused by `SendWebMessage` being called from a background timer thread — all messages are now marshalled to the UI thread via `Invoke`
* Fixed a fatal access violation in `SubclassWndProc` → `CallWindowProc` during window teardown — replaced `SetWindowLongPtr` subclassing with the correct `SetWindowSubclass` / `DefSubclassProc` / `RemoveWindowSubclass` API
* Fixed a fatal crash in `CVRSystem.PollNextEvent` when SteamVR is closed while Space Flight / VR Overlay is active — added a `volatile bool _vrQuit` guard that prevents further native calls after the quit event
* Fixed a fatal access violation (`0xC0000005`) in `CVRSystem.GetControllerState` when SteamVR exits or hard-crashes while the VR Overlay is active — `_vrSystem` is now nulled immediately on `VREvent_Quit` before acknowledging, and a background monitor (`WaitForExitAsync` on vrserver.exe) ensures the interface is invalidated instantly on hard crashes with zero poll-loop overhead

**Project Changes**

* Removed unused CSS from `styles.css`
* Merged several CSS classes to reduce redundancy
* Slightly refactored `styles.css` to improve maintainability


**i18n**

* Added missing translations for the Setup Wizard across all supported languages (DE, FR, ES, JA, ZH-CN)
* Added missing translations: Play VRChat button, In Instance sidebar section, Chatbox and Voice topbar badges, Memory Trim description
* Added missing translations: `worlds.meta.published` and `worlds.meta.updated` in the world detail modal
* Added missing translation: Check for Avatar in the context menu
* Added missing translations: Install Chrome Extension, Install Firefox Extension, How to use YouTube Fix (all steps)
* Fixed `tray.status.join_me` and `tray.status.ask_me` in Japanese still showing English
* Added `data-i18n-html` support to the i18n system for elements containing HTML markup
