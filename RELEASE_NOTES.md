## What's New in 2026.2.0

### Window and App Behavior
- The app window is now fully resizable and behaves like a standard Windows window
- Aero Snap, edge tiling, and the Windows 11 snap layout flyout all work as expected
- Double-clicking the top bar now maximizes or restores the window
- Native Windows 11 rounded corners are applied via DWM

### VRChat Integration
- Accepting an invite or clicking Join while VRChat is not running now launches VRChat and joins the target instance directly
- A modal lets you choose between VR and Desktop mode before launching, with VR pre-selected when SteamVR is detected

### Notifications
- Incoming notifications now appear as toast cards in the bottom-right corner
- Each card shows a type-specific icon, sender, world name, and message where applicable
- Cards auto-dismiss after 8 seconds with a visual countdown bar
- Actionable notifications like invites and friend requests include a quick Accept button
- Invite notifications now show the world name as a clickable link that opens the world detail view
- Invite messages are now displayed inside the notification when included

### Invite System
- Clicking Invite on a profile now opens a modal where you can choose between a direct invite or an invite with a custom message
- Invite messages can be edited, with a 60-minute rate limit per message enforced by VRChat
- VRCN shows a clear indicator when an invite message is currently rate limited

### Profile Modal
- The Favorite button now shows only an icon to save space
- The Unfriend button has been replaced with an icon-only button placed next to Block
- Pressing Unfriend now shows a confirmation prompt before performing the action
- "Invite Here" has been shortened to "Invite"

### People Tab
- Unblock and Unmute buttons are now visually distinct with a red background so they stand out from the list item
- Added a search bar to the Blocked and Muted sections to quickly find specific users

### UI and Design
- Bulk invite modal padding and styling updated to match all other modals
- All action feedback, confirmations, and status messages now use a single unified toast system
- Some CSS has been refactored to support responsive layout when resizing the window
