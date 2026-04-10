**2026.14.5**

**Modal Changes Overall**
* Increased the Modal size to make more space for new elements.
* Updated load-times for Modals. They should load a little faster now.
* Updated Cache logic for Modals to improve loading speed.
* Added FFC Caching for Favorited Worlds.

**Group Modal**

* The group owner is now shown below the group name
* Added a **Visibility** option inside the group modal
* Added a **Visibility** option to the context menu

**Profile Changes**

* Removed the **Note** text input
* Added **Edit Note** to **VRC Note**, which can now be used to add, change, or remove notes. This helps save space in the profile modal
* Updated the profile modal to look more similar to the Group, World, and your own profile modals
* Removed unused CSS
* Made the **Info** section smaller, including items like **Last Seen**, **Joined**, and **Time Together**, to create more space inside the profile modal
* Added new Navbar Section "Favs." In Favorites you can see the favorited worlds of an user or an friend if they have set it to "Friends" or "Public"

**Timeline in User Profiles**

* Added the last 10 Timeline events you had with a user. When you open a user profile and scroll down, you can now see your most recent shared events with that person, such as the latest pictures you took together, your first meeting, meeting again events, or instances where you met each other.

**Additional Apps now start separately for Desktop and VR**

* Previously, you could add startup apps that launched when starting VRChat through VRCNext. These apps would open regardless of whether you started in Desktop or VR mode.
This has now been split into two separate startup lists, so you can choose which apps launch in Desktop mode and which apps launch in VR mode.

**Download & Inspect Images**

* Right-clicking a banner or profile/group icon inside any detail modal (Profile, Group, Event, World, Avatar) now shows a context menu with **Download Image** and **Inspect Image**
* **Download Image** saves the image through your system file dialog
* **Inspect Image** opens the image in a fullscreen lightbox

**Restart after Crash**

* If VRCNext crashes due to an unexpected error (for example Steam Overlay issues or the taskbar closing it), it will now try to restart automatically. You can disable this in Settings > Debugging.

**Bug Fixes**

* Fixed a crash that could happen while closing the app window
* Fixed a crash that could happen when SteamVR was closed normally
* Cleaned up crash reports so they no longer include unrelated Windows log entries from other apps
* Fixed a rare .NET 9 runtime crash when reading VRChat log timestamps
* VRCNext has sent bug reports when the app was killed with Taskmanager which caused internal spam on my end.
* Fixed an issue that causes Start Up Apps not to be saved in Settings.
* Reduced the status text limit from 64 chars. to 32 chars. as the API only allows max 32 chars.
* Fixed an issue that caused images to not be updated when doing changes on the dashboard widget
* Fixed some dashboard related issues that cause the dashboard to show wrong contents.
* Fixed an User Profile issue that caused long load times.
* Fixed an Group Modal issue that causes infinity load times.
* Fixed some caching related issues.
* Fixed an issue that causes 429 Errors because of loading too many user requests in group modals.
* Fixed some REST API request methods to improve loading and reduce GET Requests.