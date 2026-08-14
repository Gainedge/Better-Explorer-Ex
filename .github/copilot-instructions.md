# Copilot Instructions

## Project Guidelines
- When fixing OneDrive folder thumbnail loading, the root cause was stale entries in the Windows shell thumbnail cache and the app's _thumbCache. The fix required: 1) routing cloud-backed folders through _thumbQueue not _iconQueue, 2) skipping _thumbCache stamping for cloud items in navigation flow, 3) using Storage API directly for cloud-backed folders instead of ResizeToFit which returns stale type icons, 4) detecting cloud items via FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS|FILE_ATTRIBUTE_RECALL_ON_OPEN regardless of FILE_ATTRIBUTE_PINNED