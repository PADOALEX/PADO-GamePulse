# CSV export

Use **Export CSV** in the sidebar or **Ctrl+Shift+E** on Games/recent, Compare Profiles or Inventory.
The native Save dialog asks where to write the file. Export is an explicit local action and makes no
Steam requests. No background export, account, database or telemetry is involved.

The export captures the currently filtered and sorted rows before the dialog opens. Changing a
profile, filter or page while choosing a destination cannot replace that captured table. Loading,
unavailable/private data, invalid filters and tables with no matching rows produce an availability
message. An unavailable source never becomes a fabricated zero-row report.

## Fields and interpretation

- **Games/recent:** profile SteamID, filtered scope, coverage, source update UTC, AppID, source name,
  lifetime minutes, last-14-days minutes and optional last-played UTC. Recent activity is explicitly
  `recent_activity_only`; it does not establish ownership of a complete library.
- **Mutual Games:** independent Profile A/B SteamIDs and source timestamps, filtered scope, AppID,
  name and each player's known minutes. Comparison requires both validated available libraries.
- **Inventory:** SteamID, AppID/context, filtered scope, complete/partial coverage, source timestamp,
  asset/class/instance IDs, available name/type, exact amount, nullable marketable/tradable and
  description availability. A partial inventory is labeled in every row; loaded quantities never
  establish hidden/global inventory totals.

Headers are stable English identifiers. Numeric values use invariant formatting and raw minutes,
not rounded localized hour labels. UTC timestamps use ISO 8601. Unknown fields are empty cells;
they are not `0`, `false`, an inferred name or a privacy claim. Mutual-game AppID display fallbacks remain UI-only and are not exported as names. The `complete_returned_*` scope
describes Steam's validated response, not proof that all hidden entitlements were exposed.

## Spreadsheet handling

Files use UTF-8 with a BOM, quoted cells, escaped quotes and CRLF row endings. Unicode and ordinary
embedded newlines are retained. Unexpected control characters are removed. Formula-leading values
receive a text prefix so untrusted Steam names cannot be executed as spreadsheet formulas.

SteamID64 and inventory/context identifiers receive a leading apostrophe to preserve every digit
when opened in spreadsheet applications. A machine importer can remove that documented text marker
from identifier columns. AppIDs and amounts remain invariant decimal integers.

The file contains profile identifiers and the available data you selected. Share it intentionally.
It never contains the API key, cookies or request URLs. If writing fails or the application closes
during writing, the selected file can be incomplete; the app reports failure/cancellation rather
than success. Replacing an explicitly chosen existing file is not an atomic filesystem transaction.

## Checks

`DataExportTests`, `AnalysisExportTests` and `CsvExportServiceTests` cover quoting, Unicode/newlines,
formula/control handling, 64-bit text identity, unknown/invariant values, cancellation, captured
snapshot stability, independent comparison IDs, partial inventory coverage, truncation, stream
position, UTF-8 BOM, safe storage errors and disposal. Real-view checks cover page availability and
the unavailable-table keyboard path without opening a native dialog.
