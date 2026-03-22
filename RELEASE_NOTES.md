**2026.11.4**

**Changes**

* Added a **Refresh** icon next to **Active Instances** in the World Modal to refresh the instance list.
* Optimized image caching so it takes up less space than before. Cache size should now be reduced by around 40%.
* Added **Optimize Caching** to the Image Cache. This compresses every `.png` file larger than 1.5 MB.
* Added a **Force Optimization** button to the Image Cache. If your cache is already large because of a previous VRCNext version, you can now manually run the optimization process.
* Added memory usage information with a bar to show how much space is being used by the Image Cache.

**Fixes**

* Fixed an issue that caused old instances to appear in World Modals.
* Fixed an issue that caused invalid instances to appear in World Modals.
* Fixed an issue that caused VRChat to start multiple times when the **Join** or **Play VRChat** button was used. It now launches directly through Steam and only uses the user's path as a fallback.
