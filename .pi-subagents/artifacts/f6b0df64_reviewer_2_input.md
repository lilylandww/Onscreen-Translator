# Task for reviewer

[Read from: /home/tupi/code/Onscreen-Translator/plan.md, /home/tupi/code/Onscreen-Translator/progress.md]

Thoroughly review the OnscreenTranslator.Server ASP.NET backend. Focus on:
1. Program.cs — minimal API design, endpoint contracts, JSON serialization
2. Security — no authentication middleware, API key handling, CORS
3. Error handling — exception handling, status codes, error response format
4. Provider instantiation — AppSettings singleton registration, thread safety
5. File I/O — temp file handling for OCR images, cleanup guarantees
6. The HTTP contract between the remote providers (RemoteOCRProvider, RemoteTranslationProvider) and this server — is it consistent?

Flag bugs, security issues, design flaws, and suggest concrete fixes. Be critical.

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