# Native screen-reader acceptance

Status for 0.8.0: semantic/headless checks passed; actual Narrator speech and new native live-event
delivery remain unverified. Desktop control was stopped with physical Escape during the native review.
This checklist is executable acceptance work, not a claim that every item has passed.

Use a supported Windows machine, the extracted 0.8 package, and only profiles you are allowed to inspect.
Begin without a key for the validation checks. Do not copy unrelated windows, keys or personal details
into speech transcripts. Record OS/Narrator version, UI language, theme, DPI, task, exact observed
behavior and pass/fail. A native accessibility tree alone does not prove what was spoken.

Microsoft documents reading the current item with Narrator+Tab in
[Reading text](https://support.microsoft.com/en-us/accessibility/windows/narrator/chapter-4-reading-text).
Its [complete guide](https://support.microsoft.com/en-us/accessibility/windows/narrator/complete-guide-to-narrator)
describes Narrator+Alt+X for Speech Recap/live transcription. The Narrator modifier depends on the
user's configured layout; consult [the keyboard reference](https://support.microsoft.com/en-us/accessibility/windows/narrator/appendix-b-narrator-keyboard-commands-and-touch-gestures).

| Task | Expected behavior | 0.8 native speech result |
| --- | --- | --- |
| Launch without a key | Setup guidance is reachable; profile lookup has a name and edit role | Not reviewed |
| Ctrl+L, enter `invalid display name with spaces`, Explore | Exact localized validation status becomes readable/announced without moving focus or HTTP | Not reviewed |
| Change to a different invalid input | Changed visible status has an appropriate announcement; identical consecutive text is not duplicated by app code | Not reviewed |
| Navigate all six pages with Tab/Enter | Button role, clear name and current-page status; heading level 1 on each page | Not reviewed |
| Ctrl+F on each page | Actual page filter receives focus and is visible; Overview focuses global lookup | Not reviewed |
| Achievement library sort and status | Independent controls; status does not overlap sort or take focus | Not reviewed |
| Begin an allowed load and leave the page | Hidden-page status updates do not interrupt the active page | Not reviewed |
| Large populated Games/Friends/Inventory | Virtualized rows have useful names; keyboard reaches later rows; selected details remain reachable | Not reviewed |
| Select a friend's mini-profile and Compare A/B | Chosen side changes; other side retained; controls expose meaningful names | Not reviewed |
| Achievement progress, N/A and unavailable states | Real values versus missing progress remain distinct; progress text does not repeatedly announce | Not reviewed |
| Escape with an open combo/popup | Popup closes before request cancellation; no focus trap | Not reviewed |
| Dark/light/high contrast, minimum viewport | Focus indicator remains visible and Settings/filter controls reachable | Not reviewed |
| Relaunch with `GAMEPULSE_UI_LANGUAGE=uk` | Labels/statuses use Ukrainian; app remains usable with the available system speech voice | Not reviewed |

Re-run the native provider check from the source root after desktop review is resumed:

```powershell
powershell -NoProfile -STA -ExecutionPolicy Bypass -File scripts/Check-WindowsAccessibility.ps1 -ExecutablePath <published-GamePulse.Desktop.exe> -OutputPath <fresh-report.json>
```

This script checks edit/value/focus, exact invalid-input status, navigation and DPI context. It does
not start Narrator, subscribe to native LiveRegionChanged, capture speech or establish audible output.
Do not upgrade its successful JSON into a Narrator speech claim.

For the interactive installer, verify the welcome/license/destination/optional shortcut/confirmation/
finish and cancel flow with Tab, Shift+Tab, Space, Enter and Narrator. The unattended tests already
cover actual upgrade/reinstall/guard/uninstall behavior. Run in a disposable test environment without
an existing GamePulse installation; do not replace a real user's install to perform acceptance.
The distributed 0.7 installer predates the new version guard. See [upgrade policy](INSTALLER_UPGRADE.md).

Raw Speech Recap text should be reviewed locally and reduced to task-specific evidence before
distribution. No speech transcript, recording or accessibility certification was fabricated for 0.8.
