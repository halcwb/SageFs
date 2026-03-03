# Changelog

## 0.5.457

### Bug Fixes
- Fixed `isRunning` false positive: HTTP 500 no longer counts as "running"
- Fixed `postCommand` crash on non-200 responses (guards HTTP status before JSON parse)
- Fixed TestCodeLens: clicking test results now runs tests (was empty command)
- Fixed test adapter debounce: increased from 500ms to 2000ms to prevent rapid-fire reruns
- Fixed timer leak: tree providers now clean up refresh timers on deactivate

### New Features
- Daemon crash detection: shows restart prompt when daemon stops unexpectedly
- Status bar transient messages: command results flash in status bar instead of toast notifications
- Added `sagefs.logLevel` setting for output channel verbosity

### Improvements
- Fixed Shift+Enter keybinding: no longer conflicts with Jupyter/notebook extensions
- Removed redundant toast notifications during startup
- Added extension icon for marketplace visibility
- Fixed marketplace categories: Programming Languages, Testing, Linters
- Added LICENSE file (MIT)
