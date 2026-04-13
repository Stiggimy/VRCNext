**2026.15.3**

**Media Library**

* Added **Reveal in Explorer** button to the context menu.
  This opens the file location of the image or video and highlights it in File Explorer.

**Caching Update**

* Added **Blocked** cache with a TTL of 1 day
* Added **Muted** cache with a TTL of 1 day
* Added **Favorited Avatars** cache with a TTL of 1 day
* Added **Inventory** cache with a TTL of 12 hours
* Added **Groups** Cache to user profiles with a TLL of 1 Day
* Added **Content** Cache to user profiles with a TLL of 1 Day
* Added **Favs.** Cache to user profiles with a TLL of 3 Days

These caching updates will not affect your experience in any negative way. If something new appears in your inventory or anywhere else, you can simply press the **Refresh** button, which is available throughout the app. This change is mainly there to prevent VRCN from requesting the same data repeatedly during the day. Fewer API requests to VRChat also helps reduce unnecessary load.
