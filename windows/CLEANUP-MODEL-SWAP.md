# Replacing the local cleanup model on Windows

The Mac ships Murmur Cleanup 2B (MLX safetensors). Windows ships Vedu Scribe 0.8B as a GGUF through LLamaSharp. The code is built so that putting a new GGUF in is a short, checkable change, with the same behavior the Mac has: the old model folder is deleted once the new one is verified, a ready model can be reinstalled, and the model's capabilities (what the Local route can run) come from one record.

## What the record controls

`LocalCleanupModel` (`windows/src/Murmur.Core/LocalCleanup.cs`):

| Field | Effect |
| --- | --- |
| `Name`, `ModelId`, `Repository`, `FolderName` | Row title, History model id, pin lookup in `windows/shared/windows-models.json`, folder `<data root>\Models\<FolderName>-<rev7>` |
| `RetiredFolderNames` | Folders of the models this one replaced. Deleted after the new model verifies (in `ModelFlow`) and at launch when the new one is already ready; reported in the row message while the old one lingers (`ModelSlot.ReconcileRetired`) |
| `RunsModelLevels` | Polish, Professional and Natural run on the Local model route (`CleanupChoice.RunsModelLevels`). Without it they need a Custom Provider; with it, choosing Polish or Professional from Local rules moves to the Local model |
| `RunsProfessional` | Sends `[tone: formal]` (`ProviderID.RunsProfessional`) |
| `TakesContextAndPresets` | Sends rewrite presets as `[mode: email\|notes\|prompt]` and screen context as `[screen: ...]` (`LocalModelCleanupProvider`, `CleanupShape`). Windows does not capture screen text yet, so only presets are live |
| `TrustedLikeCloud` | Polish and Professional are held only to malfunctions by the guard. Otherwise the full guard (numbers, names, negations) applies |
| `MaximumCharacters` | Longest dictation one request takes |

## Steps

1. **Convert and publish the GGUF.** `module-one/cleanup/convert_gguf.py` (see `windows/PLAN.md`, "Convert ourselves, reproducibly") for the Qwen3.5 family; check that the llama.cpp inside LLamaSharp loads it (the 0.8B needed `patch_gguf_layers.py`). Upload to a Hugging Face repository and note the revision.
2. **Pin it.** Add the repository, revision and file hashes to `windows/shared/windows-models.json` (same shape as `clouddevaakash/vedu-scribe-0.8b-gguf`). The Mac's `module-one/models/artifact-pins.json` keeps the MLX checkpoint; Windows does not read it for this model.
3. **Add the profile.** In `LocalCleanupModels`, add a record next to `VeduScribe08B`:

   ```csharp
   public static readonly LocalCleanupModel MurmurCleanup2B = new()
   {
       Name = "Murmur Cleanup 2B", ModelId = "<repo>@<rev7>", Repository = "<repo>", FolderName = "murmur-cleanup-2b",
       RetiredFolderNames = ["vedu-scribe-0.8b"],
       RunsModelLevels = true, RunsProfessional = true, TakesContextAndPresets = true, TrustedLikeCloud = true,
   };
   ```

   Then point `Installed` at it. Set only the capabilities the model was trained for (the Mac's 2B takes all of them).
4. **Helper limits.** `Murmur.Cleanup/Program.cs` caps the input at 400 tokens (the 0.8B's card). The Mac's 2B uses 512 (its training rows stayed under 400); change `MaximumInputTokens` to match the new model. The protocol already carries `tone`, `mode`, `screen` and `terms`.
5. **Tests.** The golden tests run the Mac's behavior with `LocalCleanupModels.Use(...)` on a full-capability profile; keep them. Add a Windows pin test for the new model and update the model-row summary text if the size changes. `LocalCleanupModelSwapTests` checks the retired-folder, capability and route behavior for any profile.
6. **Verify on the VM.** Download through the Settings row, check the old folder is gone, dictate with Natural, Polish, Professional and an Email preset on the Local route, and check the guard on a few raw/cleaned pairs. Compare outputs with the Mac on the same fixtures before making it the default.

The old model's folder name is the only thing the next release must keep in `RetiredFolderNames`, for as long as users on the old release can update directly.
