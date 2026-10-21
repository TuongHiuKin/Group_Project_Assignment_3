---
name: aives-ai-integration
description: Guidelines for Speech-to-Text, Text-to-Speech, and LLM follow-up generation services in AIVES Assignment 2.
---

# AIVES AI Integration Governance

This skill governs the integration of AI components (**Speech-to-Text**, **Text-to-Speech**, and **Large Language Models**) for **AIVES Assignment 2 (Group 3: AI Interview Core)**.

## 1. AI Service Interfaces & Abstractions

All AI services must be accessed through explicit BLL abstractions defined in `AIVES.BLL.Interfaces.Ai`:

1. **`ISpeechToTextService`:**
   - Transcribes student spoken audio streams (WebM / WAV) into text.
   - Returns `SpeechToTextResult` with `TranscriptText` and `ConfidenceScore`.
2. **`ITextToSpeechService`:**
   - Synthesizes question or follow-up text into spoken audio (Vietnamese / English).
   - Returns `TextToSpeechResult` containing an audio URL or byte stream.
3. **`IFollowUpGeneratorService`:**
   - Analyzes original question + student answer transcript and produces a targeted follow-up question.
   - Returns `FollowUpResult` containing `FollowUpQuestionText` and `CognitiveIntent`.

---

## 2. Mock & Real Adapter Implementations

Every AI component must provide two implementations:
1. **Mock Implementation (For Automated Tests Only when `AllowMockFallback = true`):**
   - Deterministic, fast, offline responses without requiring cloud API keys.
   - Examples: `MockSpeechToTextService`, `MockTextToSpeechService`, `MockFollowUpGeneratorService`.
   - **Note on Mock Audio & Browser Speech Synthesis:** By default (`AllowMockFallback = false`), real AI adapters do not fall back to mock services on runtime failure, returning a clear Vietnamese error instead. When `AllowMockFallback = true` is explicitly enabled for automated tests, mock TTS returns `/audio/mock-*.mp3` URLs; the client script (`viva-signalr-client.js`) ignores `/audio/mock-` URLs so it does not attempt to load a non-existent static audio file, and browser `speechSynthesis` is only auto-triggered when `audioUrl` is empty (not when a mock URL is returned).
2. **Real Cloud/API Adapter (Configurable via `appsettings.json` & `IAiCredentialStore`):**
   - E.g. OpenAI Whisper / Google Gemini for STT.
   - OpenAI TTS / Google Gemini TTS for TTS.
   - OpenAI GPT-4o-mini / Google Gemini for LLM follow-up.

---

## 3. Configuration & Secrets Management

* **No Hardcoded Keys:**
  - Never commit API keys, endpoint URLs with auth tokens, or private secrets in code.
  - Read configuration from `appsettings.json` sections (`AiServices:OpenAI`, `AiServices:AzureSpeech`).
  - Use environment variables or `dotnet user-secrets` in local environments.
* **Logging Guardrails:**
  - Do not log raw audio binary payloads or personally identifiable voice recordings into application logs.
  - Log only operation latency, token usage, confidence scores, and high-level success/failure status.

---

## 4. Timeout, Cancellation & Error Handling

* **Timeouts:**
  - STT operations must timeout after **15 seconds**.
  - TTS operations must timeout after **10 seconds**.
  - LLM follow-up generation must timeout after **15 seconds**.
* **Cancellation Tokens:**
  - All methods must accept and pass through `CancellationToken`. If the student cancels or disconnects, the ongoing HTTP call to the AI provider must be aborted immediately to conserve quota.
* **Graceful Degradation:**
  - If STT fails, return `SpeechToTextResult.Failed(message)` allowing the BLL to emit a `VivaError` event with `CanRetry = true`.
  - If LLM follow-up generation fails, the system must gracefully skip the follow-up step and allow the student to advance rather than crashing the examination.

---

## 5. Scope Boundaries (Strict Assignment 2 Rule)

* **NO SCORING IN ASSIGNMENT 2:**
  - Group 4 (AI Scoring, grading, and rubric evaluation) is **STRICTLY OUT OF SCOPE** for Assignment 2.
  - The LLM service in Assignment 2 is solely used for generating **contextual follow-up questions** (`IFollowUpGeneratorService`).
* **Follow-Up Count Limits Enforced by BLL:**
  - The AI prompt may propose follow-ups, but the BLL `VivaSessionOrchestrator` is the **authoritative arbiter** of when follow-ups terminate (maximum 1 or 2 follow-ups per main question).
