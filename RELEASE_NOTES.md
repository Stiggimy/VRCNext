**2026.15.2**

**Auto Updating VRCNext**
* VRCN Auto-Updates by default now. You can turn of that setting in the settings tab.

**Caching Update**
* Added Avatars Cache
* Added Groups Cache
* Added Favorited Worlds Cache
* Added Favorited Friends Cache
* Added Friends Cache
* Added Mutuals Cache
These have a lifetime of 1 day. It is just there to prevent multiple GET Request on every start up. Those refresh after one day or when you manually refresh them.

**Fallback Header**

* Added fallback images to Group Modals
* Added fallback images to Group Event Modals
* Added fallback images to Group Events on the Dashboard

**Calendar**

* Event modals now have an **Open Group** button next to the **Follow** button
* Event modals now show the group name at the top of the event

**Dashboard**

* Added a new **Upcoming Events** widget that shows the next 3 upcoming events on your dashboard

**Date and Time Refactor**

* Added `DateTimeHelper` to format date and time based on the user's current system settings
* If the system uses AM/PM, times will now be shown in AM/PM format
* If the system uses US date formatting, dates will now follow that format as well

**Profiles**

* Added **Details** to the timeline section in user profiles to show more information
* Added **Last Active**, which shows when a user was last active on the VRChat website or in-game

**Changes**

* Added missing i18n for several profile-related items

**Fixes**

* Fixed an issue where the crash logger would not write a crash log file in certain cases
* Fixed some date mismatches between the VRChat REST API and the local machine
* Fixed the **Last Seen** value in user profiles. It now works correctly
* Fixed a bug with Group Events where only the start date and time were shown, but not the end date and time
* Fixed crucial bug that caused spaming GET Requests for images even tho cached data existed already.
* Fixed an caching issue with profiles, groups and events
* Fixed GET spam on startup for groups and events.
* Fixed an issue where disabled Dashboard Widgets made GET Requests even though they aren't visible at all.
* Fixed an issue that caused the reports to show a wrong VRCN version on my end.

**Internal Changes**

* Removed hardcoded `en-GB`, `en-US`, and `de-DE` date formats
* Removed hardcoded `en-GB`, `en-US`, and `de-DE` time formats
* Added `DateTimeHelper` to format date and time correctly for the end user
* Reverted some changes to the Watchdog log handler
