# Task for worker

[Read from: /home/tupi/code/Onscreen-Translator/context.md, /home/tupi/code/Onscreen-Translator/plan.md]

You are a delegated subagent running from a fork of the parent session. Treat the inherited conversation as reference-only context, not a live thread to continue. Do not continue or answer prior messages as if they are waiting for a reply. Your sole job is to execute the task below and return a focused result for that task using your tools.

Task:
Fix critical and major issues in OnscreenTranslator.Core based on code review findings. The files to edit are under /home/tupi/code/Onscreen-Translator/OnscreenTranslator.Core/. Make ALL of the following fixes:

1. DictionaryLookup.cs: Fix sync-over-async deadlock — make LookUp() properly async. The `responseTask.Wait()` and `.Result` will deadlock on UI threads. Make the method use async/await throughout.

2. ProviderFactory.cs line ~18: Add "remote" to AvailableTranslationProviders array (it's currently missing but display names and CreateTranslationProvider handle it).

3. ProviderFactory.cs CreateOcrProvider default case: Throw ArgumentException for unknown provider names (currently silently falls back to Ollama). Match CreateTranslationProvider behavior.

4. ProviderFactory.cs CreateTranslationProviders (plural): Add RemoteTranslationProvider creation (currently missing, only creates 5 providers).

5. RemoteOCRProvider.cs and RemoteTranslationProvider.cs: Fix IsAvailableAsync() to actually check server health via GET /api/health instead of just checking BaseAddress != null.

6. FuriganaServiceManager.cs OnProcessExited: Wrap the entire async void body in try-catch to prevent process crash from unhandled exceptions.

7. DeepLTranslationProvider.cs: Pass the CancellationToken through to TranslateTextAsync call.

8. Create a new file Providers/OcrOutputCleaner.cs: Extract the duplicated CleanOcrOutput method (nearly identical in OllamaOCRProvider and OpenAIOCRProvider) into a shared static utility class. Then update both providers to use it.

9. Settings/AppSettings.cs: Fix ProtectedStringConverter — when encryption fails, log a warning via Debug.WriteLine and still encrypt with XOR fallback rather than silently storing plaintext. Also improve the entropy to be per-installation (use machine name hash + fixed prefix).

10. Settings/AppSettings.cs: Integrate RemoteServerConfig.RouteOcr/RouteTranslation/RouteDictionary — actually use them. When RouteOcr is false but provider is "remote", the factory should throw or fall back to default. Or better: remove these dead config properties and simplify to just Enabled + BaseUrl + ApiKey.

11. Move IModelListable interface from Interfaces/IOCRProvider.cs into its own file Interfaces/IModelListable.cs and update all usings.

After each fix, verify the Core project still builds: dotnet build OnscreenTranslator.Core/OnscreenTranslator.Core.csproj

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