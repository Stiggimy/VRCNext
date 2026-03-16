# Release Notes

## 2026.10.20

### Timeline

**Fixes**
- Fixed an issue where the "Online" event fired when someone came online through the website instead of the game.
- Fixed an issue where the "Offline" event fired when someone went offline through the website instead of the game.
- Online and Offline events now only fire when someone actually joins or leaves the game.

**Changes**
- Added separate "Online (Game)" and "Offline (Game)" event types for in-game activity.
- Added separate "Online (Web)" and "Offline (Web)" event types for website activity.
- Added filter buttons for all four new event types.
- Status badges now use the official VRChat colors for easier readability.
- Status text changes now display the actual new status text.
- Bio changes now display the actual new bio text.

### Steam VR Overlay

**Fixes**
- Fixed an issue where the "Went Online" event was spammed when a user changed their online status.
- Fixed a false "User went online" event caused by website activity being treated as a game login.
- Fixed a false "User went offline" event caused by website activity being treated as leaving the game.
- Fixed the "in a world" placeholder appearing before the actual world name loaded.

**Changes**
- Notification badges now show the event type (e.g., "Status", "Status Text", "Bio", "Location") next to the friend name for better clarity.
- Tab buttons are now smoothly animated when switching pages.
- Media player progress bar is now seekable — tap or slide to change the track position.

**New: Notification Breadcrumbs**
- HMD-attached toast notifications that appear in front of you when friend events happen.
- Configurable per event type:
  - Friend comes online (Game)
  - Friend goes offline (Game)
  - Friend joined a world
  - Friend changed status
  - Friend changed status text
  - Friend updated bio
- Settings available under VR Overlay > Overlay Notifications:
  - Enable / Disable
  - Favorites only filter
  - Size slider
  - Position offset (X / Y)
- Toast design: themed card with avatar, event badge, progress bar, fade-in/out animation.
- Notification sound plays when a toast appears (configurable in Settings > Sounds).

### World Insights

**Fixes**
- Fixed calendar navigation.
- Fixed missing dots in the Months category.
- Uses wide pill format for buttons to match modal style.

---

## 2026.10.17-20 Implementations

* **World Insights:** If you have a world uploaded, you will now see an *Insights* tab showing statistics. Data is only collected while VRCN is running.

**Fixes**

* Fixed a compression issue.
* Fixed a Steam Overlay issue that caused locations to not update.
* Fixed a cache problem with PNG files.
* Fixed an issue where badges were converted to JPG instead of remaining alpha PNG.
