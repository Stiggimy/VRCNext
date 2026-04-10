**2026.14.2**

**Additional Apps now start separately for Desktop and VR**

Previously, you could add startup apps that launched when starting VRChat through VRCNext. These apps would open regardless of whether you started in Desktop or VR mode.
This has now been split into two separate startup lists, so you can choose which apps launch in Desktop mode and which apps launch in VR mode.

**Download & Inspect Images**

* Right-clicking a banner or profile/group icon inside any detail modal (Profile, Group, Event, World, Avatar) now shows a context menu with **Download Image** and **Inspect Image**
* **Download Image** saves the image through your system file dialog
* **Inspect Image** opens the image in a fullscreen lightbox

**Restart after Crash**

If VRCNext crashes due to an unexpected error (for example Steam Overlay issues or the taskbar closing it), it will now try to restart automatically. You can disable this in Settings > Debugging.

**Bug Fixes**

* Fixed a crash that could happen while closing the app window
* Fixed a crash that could happen when SteamVR was closed normally
* Cleaned up crash reports so they no longer include unrelated Windows log entries from other apps
* Fixed a rare .NET 9 runtime crash when reading VRChat log timestamps
* VRCNext has sent bug reports when the app was killed with Taskmanager which caused internal spam on my end.
