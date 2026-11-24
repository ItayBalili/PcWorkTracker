# Dashboard and export

The dashboard refreshes local persisted observations every 30 seconds and after work-day
actions. The date picker and period tabs select a day, Sunday–Saturday week, or calendar
month. Ctrl+F focuses search, Ctrl+E opens export, and the arrow keys navigate periods.

The daily timeline uses a single chronological scale from the earliest start to the latest
end; gaps are neither active nor away. Hover a segment for its application or away label
and timestamps. Displayed time may overlap across windows and exceed work-day duration.

CSV exports use UTF-8 with BOM; XLSX exports use Excel. Both contain period totals, per-day
boundaries and metrics, and the currently filtered and sorted application/title groups.
Application totals remain the totals shown in the table even when a title search hides
some titles. Export requires an explicit local destination and confirms overwriting.
After a successful export, the notification includes a link to open its folder. Long
table values remain available using the table's horizontal scrollbar when needed.
Failures are recorded in `%LOCALAPPDATA%\WorkTimeTracker\worktimetracker.log` when
available.
