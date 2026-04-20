**2026.16.5**

**VR Overlay**

* Doubled the overlay rendering resolution to 1024×768 instead of 512×384 for a sharper image when holding the overlay close.

**Fixes**

* Fixed an issue where the **Accept** and **Join** buttons in the VR Overlay notifications tab did not respond to clicks.
* Fixed a native crash on app close (`0xC0000005` Access Violation) caused by incorrect `WM_NCDESTROY` handling during window subclass teardown.
* Fixed an SQLite crash on app close caused by a `NullReferenceException` when disposing the timeline database connection.
* Fixed all date pickers and calendars showing Sunday as the first day of the week. They now all start on Monday.
* Fixed missing world images on Timeline pages 2 and beyond. All pages now run the same world enrichment logic as page 1.
* Fixed missing world images on Friend Timeline pages 2 and beyond by applying the same fix there as well.
* Removed the 20-world-per-page cap on world thumbnail enrichment. All distinct worlds on a page are now resolved.
* Removed negative caching for failed world API calls. Transient failures no longer permanently block world images for the rest of the session.
* Added a world thumbnail cache fallback in the payload builder so the same world ID always returns the same image, regardless of which page the event appears on.

**Media Relay**

* Relay Control and Bot Identity are now displayed as side-by-side cards.
* The Webhook Channels section is now also shown as a card to keep it consistent with the rest of the UI.

**Time Spent**

* Removed the 200-entry cap for Worlds and Persons. All unique entries are now shown.
* Added pagination to both Worlds and Persons views with 100 entries per page.
* Search now queries the full dataset server-side, just like Timeline, instead of only searching the current page.
* Missing world thumbnails and person images on the current page are now automatically backfilled from the API after loading.
* Fixed search flickering and aggressive re-rendering on every keystroke. Search now uses the same 300ms debounce, skeleton loading animation, and stale-response handling as the Timeline search.
