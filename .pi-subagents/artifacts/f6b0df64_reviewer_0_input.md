# Task for reviewer

[Read from: /home/tupi/code/Onscreen-Translator/plan.md, /home/tupi/code/Onscreen-Translator/progress.md]

Thoroughly review the OnscreenTranslator.Core library code. Focus on:
1. Interface design (IOCRProvider, ITranslationProvider, IPlatformServices) — are the abstractions clean, future-proof, and platform-agnostic?
2. Provider implementations — correctness, error handling, threading/async issues, HttpClient lifecycle
3. RemoteOCRProvider and RemoteTranslationProvider — security concerns, proper URL handling, auth
4. AppSettings design — JSON serialization, ProtectedStringConverter cross-platform fallback, RemoteServerConfig
5. ProviderFactory — extensibility, the new 'remote' case, switch completeness
6. FuriganaServiceManager — process lifecycle, error recovery, the async fix that was applied
7. Platform/IPlatformServices.cs — is the abstraction sufficient for Windows and Linux?

Flag bugs, design flaws, missing edge cases, and suggest concrete fixes. Be critical.

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