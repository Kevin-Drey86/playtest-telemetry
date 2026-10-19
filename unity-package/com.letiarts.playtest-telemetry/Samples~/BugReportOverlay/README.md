# Bug Report Overlay sample

Add `BugReportOverlay` to a GameObject that exists in every scene (or mark it DontDestroyOnLoad).
Open the form with a **three-finger tap**, a **device shake**, or **F8** in the Editor.
The form is plain on purpose: copy the script and reskin it. The only telemetry call it makes is
`PlaytestTelemetry.ReportBug(note, includeScreenshot)`.
