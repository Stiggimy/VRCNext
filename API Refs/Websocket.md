```markdown
# VRChat WebSocket API ("Pipeline")

## Overview
VRChat's WebSocket API (also known as the **pipeline**) is used to receive real-time updates such as invites, friend activity, and notifications.

- **Receive-only**: Sending messages is undefined behavior
- **Multiple connections allowed**: All clients receive identical messages
- **Endpoint**:
```

wss://pipeline.vrchat.cloud/?authToken=authcookie_...

````
- Requires:
- Valid `authToken` (from login cookie)
- Proper `User-Agent`

---

## Important Notes

### Double-Encoded Messages
Most messages are **double-encoded**:

```json
{
"type": "notification",
"content": "{\"userId\": \"usr_...\"}"
}
````

* `content` is a **stringified JSON object**
* Must be parsed twice

---

## Common Enumerations

### `:locationString`

* `""` → pseudo-null
* `offline` → user not connected
* `traveling` → switching instances
* `private` → hidden location
* other → actual instance location

### `:platformString`

* `""` → pseudo-null
* `standalonewindows`
* `android`
* `web`
* other → third-party / future platforms

### `:contentRefreshContentTypeEnum`

* `gallery`, `icon`, `emoji`, `print`, `prints`
* `sticker`, `inventory`, `avatar`, `world`

### `:contentRefreshActionTypeEnum`

* `created`, `deleted`
* `add`, `delete` (inventory only)

### `:inventoryType`

* `sticker`, `emoji`, `bundle`, `prop`

---

# Events

## Notification Events

### `notification`

Carries a Notification object.

```json
{
  "type": "notification",
  "content": { }
}
```

---

### `response-notification`

Response to a notification.

```json
{
  "type": "response-notification",
  "content": {
    "notificationId": ":notificationId",
    "receiverId": ":userId",
    "responseId": ":notificationId"
  }
}
```

---

### `see-notification`

Mark notification as seen.

```json
{
  "type": "see-notification",
  "content": ":notificationId"
}
```

---

### `hide-notification`

Hide a notification.

```json
{
  "type": "hide-notification",
  "content": ":notificationId"
}
```

---

### `clear-notification`

Clear all notifications.

```json
{
  "type": "clear-notification"
}
```

---

## Notification V2 Events

### `notification-v2`

```json
{
  "type": "notification-v2",
  "content": {
    "id": ":notificationId",
    "version": 2,
    "type": ":notificationV2TypeEnum",
    "category": ":notificationV2CategoryEnum",
    "isSystem": true,
    "ignoreDND": false,
    "senderUserId": ":userId",
    "receiverUserId": ":userId",
    "title": ":string",
    "message": ":string",
    "responses": []
  }
}
```

---

### `notification-v2-update`

```json
{
  "type": "notification-v2-update",
  "content": {
    "id": ":notificationId",
    "version": 2,
    "updates": { }
  }
}
```

---

### `notification-v2-delete`

```json
{
  "type": "notification-v2-delete",
  "content": {
    "ids": [":notificationId"],
    "version": 2
  }
}
```

---

## Friend Events

### `friend-add`

User accepted or sent friend request.

```json
{
  "type": "friend-add",
  "content": {
    "userId": ":userId",
    "user": { }
  }
}
```

---

### `friend-delete`

User removed or was removed as friend.

```json
{
  "type": "friend-delete",
  "content": {
    "userId": ":userId"
  }
}
```

---

### `friend-online`

Friend came online (in-game).

```json
{
  "type": "friend-online",
  "content": {
    "userId": ":userId",
    "platform": ":platformString",
    "location": ":locationString",
    "canRequestInvite": true,
    "user": { }
  }
}
```

---

### `friend-active`

Friend active on website.

```json
{
  "type": "friend-active",
  "content": {
    "userId": ":userId",
    "platform": "web",
    "user": { }
  }
}
```

---

### `friend-offline`

Friend went offline.

```json
{
  "type": "friend-offline",
  "content": {
    "userId": ":userId",
    "platform": ""
  }
}
```

---

### `friend-update`

Friend profile updated.

```json
{
  "type": "friend-update",
  "content": {
    "userId": ":userId",
    "user": { }
  }
}
```

---

### `friend-location`

Friend changed instance.

```json
{
  "type": "friend-location",
  "content": {
    "userId": ":userId",
    "location": ":locationString",
    "travelingToLocation": ":locationString",
    "worldId": ":worldId",
    "canRequestInvite": true,
    "user": { }
  }
}
```

---

## User Events

### `user-update`

```json
{
  "type": "user-update",
  "content": {
    "userId": ":userId",
    "user": {
      "displayName": ":displayName",
      "status": ":statusEnum"
    }
  }
}
```

---

### `user-location`

```json
{
  "type": "user-location",
  "content": {
    "userId": ":userId",
    "location": ":locationString",
    "instance": ":instanceId"
  }
}
```

---

### `user-badge-assigned`

```json
{
  "type": "user-badge-assigned",
  "content": {
    "badge": ":badge"
  }
}
```

---

### `user-badge-unassigned`

```json
{
  "type": "user-badge-unassigned",
  "content": {
    "badgeId": ":badgeId"
  }
}
```

---

### `content-refresh`

```json
{
  "type": "content-refresh",
  "content": {
    "contentType": ":contentRefreshContentTypeEnum",
    "fileId": ":id",
    "actionType": ":contentRefreshActionTypeEnum"
  }
}
```

---

### `modified-image-update`

```json
{
  "type": "modified-image-update",
  "content": {
    "fileId": ":id",
    "pixelSize": 0,
    "versionNumber": 0,
    "needsProcessing": false
  }
}
```

---

### `instance-queue-joined`

```json
{
  "type": "instance-queue-joined",
  "content": {
    "instanceLocation": ":locationString",
    "position": 0
  }
}
```

---

### `instance-queue-ready`

```json
{
  "type": "instance-queue-ready",
  "content": {
    "instanceLocation": ":locationString",
    "expiry": ":dateTimeString"
  }
}
```

---

## Group Events

### `group-joined`

```json
{
  "type": "group-joined",
  "content": {
    "groupId": ":groupId"
  }
}
```

---

### `group-left`

```json
{
  "type": "group-left",
  "content": {
    "groupId": ":groupId"
  }
}
```

---

### `group-member-updated`

```json
{
  "type": "group-member-updated",
  "content": {
    "member": { }
  }
}
```

---

### `group-role-updated`

```json
{
  "type": "group-role-updated",
  "content": {
    "role": { }
  }
}
```

---

# Quick Event Summary

| Event          | Description                  |
| -------------- | ---------------------------- |
| friend-active  | Friend active on website     |
| friend-offline | Friend went offline          |
| friend-online  | Friend came online (in-game) |
| friend-delete  | Friend removed               |

---
