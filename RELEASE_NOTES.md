# 2026.10.8

## UI Improvements

- Profile, Group, and World modals have been resized to be slightly larger for better readability
- Mutuals are now displayed in two rows
- Group members are now displayed in two rows
- Groups inside the profile modal are now displayed in two rows
- Added a search bar to the "Mutuals" tab in the profile modal

---

## Group Management Update

### Roles
- You can now view all roles in a group
- You can now create a role with full general and permission settings
- You can assign a role to a member via the right-click context menu

### Banned Members
- Banned members are now visible in the group management view
- You can unban members directly via the context menu

### Group Modal
- The group modal has been enlarged so all management settings fit comfortably
- Added a search bar on the "Members" tab to quickly find members — especially useful for moderation

---

## New Features

### Auto Color
Settings now include an **Auto Color** option. The launcher will automatically pick its accent color based on your dashboard background image, keeping the UI consistent with your personal design.

### Context Menu — Media Library
- Right-clicking images in the Media Library now opens a context menu
- You can quickly set your dashboard background directly from the context menu

---

## Bug Fixes

- Fixed an issue where the dashboard background image was not saved correctly
- Fixed an issue where the Invite navigation bar was cut off when using "Send with Image" — the nav bar now has its own container
- Fixed a bug that caused the media library file list to be rebuilt every time the tab was opened
- Fixed an issue where favorite images were not fully displayed if the page had not finished building
- Fixed an issue where pressing Refresh in the media library caused images to appear duplicated in the preview

---

## Linux Port

The Linux port is actively in progress. This release includes the following Linux-specific work:

- All VRChat launch paths (Join, Play VRChat, and related buttons) now correctly route through Steam (`steam steam://rungameid/438100`) instead of trying to execute `launch.exe` directly
- **Custom Chatbox** partially working on Linux:
  - **System Stats** now reads CPU usage from `/proc/stat` and RAM from `/proc/meminfo`
  - **Now Playing** and **Play Time** now work via MPRIS2 using `playerctl` (install with `sudo pacman -S playerctl`)
- Setup Wizard updated for Linux: VRChat path page shows the Steam launch command and hides the Browse button; "Start with Windows" renamed to "Start with System" throughout
- Settings panel: VRChat launch path is locked to the Steam command on Linux; Browse button is hidden
