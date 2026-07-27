# Task for worker

[Read from: /home/tupi/code/Onscreen-Translator/context.md, /home/tupi/code/Onscreen-Translator/plan.md]

You are a delegated subagent running from a fork of the parent session. Treat the inherited conversation as reference-only context, not a live thread to continue. Do not continue or answer prior messages as if they are waiting for a reply. Your sole job is to execute the task below and return a focused result for that task using your tools.

Task:
Fix critical and major issues in OnscreenTranslator.Server based on code review findings. The main file is /home/tupi/code/Onscreen-Translator/OnscreenTranslator.Server/Program.cs. Make ALL of the following fixes:

1. Fix temp file leak: Replace Path.GetTempFileName() + ".png" with Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png") to avoid creating an orphaned .tmp file.

2. Add authentication middleware: Read Authorization header, validate Bearer token against settings.RemoteServer.ApiKey (skip auth for /api/health). Return 401 if invalid. Use app.Use() middleware before the endpoint mappings.

3. Fix model parameter dead code: Either pass the model from the request to the OCR provider, or remove the parsing if not needed. Simplest: remove the dead parsing line.

4. Fix HTTP 500 → 400 for missing fields: Add explicit TryGetProperty checks before accessing required JSON fields. Return 400 with descriptive error if missing.

5. Sanitize error messages: Don't return ex.Message to clients. Log the full exception via Debug.WriteLine and return a generic "Internal server error" message.

6. Dispose providers: Wrap provider creation in `using` statements so HttpClient instances are properly disposed after each request (prevents socket exhaustion).

7. Add request body size limit: Configure a reasonable MaxRequestBodySize (e.g., 10MB) to prevent OOM from large base64 payloads.

8. Add a global exception handler middleware as fallback for unhandled exceptions outside the endpoint try-catch blocks.

Also add a basic appsettings.json with default Kestrel port binding and logging config.

After each fix, verify the Server project still builds: dotnet build OnscreenTranslator.Server/OnscreenTranslator.Server.csproj

Be thorough — read the file, make precise edits, verify build.

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