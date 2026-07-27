# Task for worker

[Read from: /home/tupi/code/Onscreen-Translator/context.md, /home/tupi/code/Onscreen-Translator/plan.md]

You are a delegated subagent running from a fork of the parent session. Treat the inherited conversation as reference-only context, not a live thread to continue. Do not continue or answer prior messages as if they are waiting for a reply. Your sole job is to execute the task below and return a focused result for that task using your tools.

Task:
Fix critical and major issues in OnscreenTranslator.Linux based on code review findings. The files are under /home/tupi/code/Onscreen-Translator/OnscreenTranslator.Linux/. Make ALL of the following fixes:

1. OnscreenTranslator.Linux.csproj: Add System.Drawing.EnableUnixSupport runtime config so Bitmap APIs work on Linux. Add to csproj:
   <ItemGroup>
     <RuntimeHostConfigurationOption Include="System.Drawing.EnableUnixSupport" Value="true" />
   </ItemGroup>

2. Platform/LinuxPlatformServices.cs: Fix temp file leak — replace Path.GetTempFileName() + ".png" pattern with Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png") to avoid creating an orphaned .tmp file.

3. Platform/LinuxPlatformServices.cs: Remove the dead `var args = ...` block (lines ~43-47) that's computed but never used.

4. Platform/LinuxPlatformServices.cs: Add HasTool("grim") check in the Wayland path (like X11 checks for maim). If grim is not installed, throw a clear error message telling the user to install grim.

5. Platform/LinuxPlatformServices.cs: Fix RunCommandAndRead to read stderr asynchronously to prevent pipe-buffer deadlock. Also remove the shell wrapping when not needed, or use safer argument passing.

6. Platform/LinuxPlatformServices.cs: In RunCommand, check exit code after the process exits and include stderr in the error message if the tool failed.

7. Views/MainWindow.axaml.cs: Fix temp file leak — same Path.GetTempFileName() fix as above.

8. Views/MainWindow.axaml.cs: Add Task.Delay(200) between this.Hide() and the screenshot capture to ensure the compositor has time to hide the window.

9. Views/MainWindow.axaml.cs: Make remote server config fields (URL, API key, checkboxes) save to _settings immediately when changed (via TextChanged/Checked events), not just when Settings button is clicked. This makes UX consistent with the combo box changes.

10. Views/MainWindow.axaml.cs: Add user feedback after SaveSettingsFromUi — show a brief status message or at minimum call _settings.Save() with a try-catch that shows errors.

After each fix, verify the Linux project still builds: dotnet build OnscreenTranslator.Linux/OnscreenTranslator.Linux.csproj

Be thorough — read each file, make precise edits, verify build.

---
Update progress at: /home/tupi/code/Onscreen-Translator/.pi-subagents/artifacts/progress/80bc7478/progress.md

## Acceptance Contract
Acceptance level: checked
Completion is not accepted from prose alone. End with a structured acceptance report.

Criteria:
- criterion-1: Implement the requested change without widening scope

Required evidence: changed-files, tests-added, commands-run, residual-risks, no-staged-files

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