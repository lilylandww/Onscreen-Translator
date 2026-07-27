# Task for reviewer

[Read from: /home/tupi/code/Onscreen-Translator/plan.md, /home/tupi/code/Onscreen-Translator/progress.md]

Thoroughly review the OnscreenTranslator.Linux Avalonia UI code. Focus on:
1. Program.cs — startup flow, platform services wiring, correctness
2. App.axaml / App.cs — Avalonia application lifecycle, design-time support
3. MainWindow.axaml / MainWindow.axaml.cs — UI binding, event handlers, async capture flow, remote config panel visibility logic
4. LinuxPlatformServices.cs — screenshot commands (maim/grim/import fallback), IsWayland() detection, XorCrypt, RunCommand robustness
5. GtkOverlayHandle.cs — the stub implementation, what's missing for real overlay rendering
6. LinuxHotkeyRegistration.cs — polling approach, whether it's viable
7. LinuxTrayIcon.cs — stub completeness

Flag bugs, architectural problems, X11/Wayland compatibility gaps, and suggest concrete fixes. Be critical.

## Acceptance Contract
Acceptance level: attested
Completion is not accepted from prose alone. End with a structured acceptance report.

Criteria:
- criterion-1: Return concrete findings with file paths and severity when applicable

Required evidence: review-findings, residual-risks

Finish with a fenced JSON block tagged `acceptance-report` in this shape:
Use empty arrays when no items apply; array fields contain strings unless object entries are shown.
`criteriaSatisfied[].status` must be exactly one of: satisfied, not-satisfied, not-applicable.
`commandsRun[].result` must be exactly one of: passed, failed, not-run.
`manualNotes` and `notes` are optional strings; an empty string means no note and does not satisfy `manual-notes` evidence.
```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "specific proof"
    }
  ],
  "changedFiles": [
    "src/file.ts"
  ],
  "testsAddedOrUpdated": [
    "test/file.test.ts"
  ],
  "commandsRun": [
    {
      "command": "command",
      "result": "passed",
      "summary": "short result"
    }
  ],
  "validationOutput": [
    "validation output or concise summary"
  ],
  "residualRisks": [
    "none"
  ],
  "noStagedFiles": true,
  "diffSummary": "short description of the diff",
  "reviewFindings": [
    "blocker: file.ts:12 - issue found, or no blockers"
  ],
  "manualNotes": "anything else the parent should know"
}
```