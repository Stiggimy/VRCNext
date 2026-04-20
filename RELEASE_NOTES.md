**2026.16.3**

**VR Overlay**
* Doubled the overlay rendering resolution (1024×768 instead of 512×384) for a sharper image when holding the overlay close.

**Fixes**
* Fixed VR overlay notifications tab Accept and Join buttons were not responding to clicks.
* Fixed a native crash (0xC0000005 Access Violation) on app close caused by incorrect WM_NCDESTROY handling in the window subclass teardown.
* Fixed SQLite crash on app close (NullReferenceException when disposing the timeline database connection).
* Fixed all date pickers and calendars showing Sunday as the first day of the week — all now start on Monday.

**Media Relay**
* Relay Control and Bot Identity are now displayed as cards side by side.
* Webhook Channels section is now also a card, consistent with the rest of the UI.
