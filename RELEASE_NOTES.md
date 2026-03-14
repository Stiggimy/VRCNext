# Release Notes

## 2026.10.13

- Fixed an issue that caused profile images in the timeline to be empty
- Fixed an issue that caused profile images in the media library to be empty
- Improved caching for profile images
- Improved caching for profile images of non-friends
- Database now stores CDN URLs to allow re-downloading profile images
- Fixed an issue that caused the paginator on the timeline to jump when selecting a date
- Fixed an issue that caused the timeline to show more than 100 results on one page
- Changed the paginator to always show 3 buttons in the middle without growing when navigating pages
- Added a background downloader for profile images used in the timeline and media library — only downloads what is needed to prevent API spam
- Changed the minimum cache size from 2 GB to 5 GB
- All images are now compressed to 90% JPEG quality to prevent the cache from filling up too quickly
- Added custom cursor support in theme settings
