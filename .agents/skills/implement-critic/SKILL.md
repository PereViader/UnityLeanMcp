---
name: implement-critic
description: >-
  Implement a feature request in a dedicated implementer subagent and iterate
  with an independent read-only critic subagent until both agree the feature is
  correct and complies with project rules. Use when the user asks to implement a
  feature with independent critic review or invokes "implement-critic".
---

# Implement-Critic Loop

Orchestrate an iterative implementer-critic workflow where one subagent builds
and verifies the feature while an independent read-only critic subagent reviews
the actual diff until both reach agreement.

## Subagent Roles

Configure two distinct subagents with `define_subagent`:

- **`feature-implementer`** (`EnableWriteTools: true`, `EnableMCPTools: true`)
  - Reads [AGENTS.md](../../../AGENTS.md) and relevant topic guides routed from
    [docs/README.md](../../../docs/README.md).
  - Implements the requested feature cleanly and runs real build/test verification.
  - Responds rigorously to critic feedback: fixes every valid defect and re-runs
    verification, or pushes back only with concrete code, doc, or test evidence
    (never blind agreement or performative argument).
  - Reports touched files, exact verification commands/results, and any remaining
    concerns.
- **`feature-critic`** (`EnableWriteTools: false`, read-only)
  - Independent reviewer starting from a clean context (never pass the
    implementer's internal reasoning on the initial review pass).
  - Reads [AGENTS.md](../../../AGENTS.md), relevant [docs/README.md](../../../docs/README.md)
    guides, the original feature request, the actual diff/changed files, and
    verification outputs.
  - Evaluates requirement completeness, correctness and edge cases, code quality,
    project rule/invariant compliance, and test coverage.

## Execution Loop

1. **Baseline & Setup**
   - Record the feature request and starting repository state (`git rev-parse HEAD`
     and `git status --short`).
   - Define `feature-implementer` and `feature-critic` with `define_subagent`.

2. **Implement**
   - Launch `feature-implementer` via `invoke_subagent` with the feature request
     and instructions to consult `AGENTS.md` and relevant `docs/*.md` guides,
     implement the feature, run verification, and report touched files,
     verification results, and any concerns.
   - Wait for its reply and keep its `conversationId`.

3. **Independent Review**
   - Capture the actual workspace changes (`git status --short`, `git diff`, and
     any newly added files).
   - Launch `feature-critic` via `invoke_subagent` (fresh context) with **only**:
     - The original feature request
     - The actual diff and changed file paths
     - The verification commands and results
     - Instructions to verify compliance against `AGENTS.md` and relevant `docs/*.md`
   - Require the critic to reply with:
     - `VERDICT: APPROVED` or `VERDICT: CHANGES_REQUESTED`
     - **Blocking Reasons** *(required for `CHANGES_REQUESTED`)*: File/line
       references, violated requirement or project rule/invariant, and concrete issue.
     - **Non-blocking Notes** *(optional)*: Minor observations clearly separated
       so they do not block approval.

4. **Collaborate Until Agreement**
   - If `VERDICT: APPROVED` and `feature-implementer` has no unresolved concerns,
     exit the loop and go to Step 6.
   - If `VERDICT: CHANGES_REQUESTED`:
     - Send the critic's blocking reasons to the existing `feature-implementer`
       via `send_message` (reusing its `conversationId`). Instruct it to fix valid
       blocking issues and re-run verification, or return concrete evidence-backed
       technical rebuttals for any factually incorrect critique.
     - After `feature-implementer` replies, capture the updated diff and latest
       verification output and send them (along with any rebuttal) to the existing
       `feature-critic` via `send_message` to re-verify blocking items and check
       for regressions.
     - Repeat Step 4 until `feature-critic` returns `VERDICT: APPROVED` with zero
       unresolved blocking issues and `feature-implementer` agrees.

5. **Convergence / Deadlock Guardrail**
   - If both subagents repeat the same unresolved disagreement or fail to
     converge after 5 review rounds, stop the loop and escalate to the user with
     the exact disagreement and both agents' concrete evidence. (This caps
     non-converging debate rounds, not elapsed wall-clock time.)

6. **Final Verification & Summary**
   - Ensure the final build/test verification actually ran after the last edit
     and passed.
   - Summarize the implemented changes, review rounds, blocking issues resolved,
     and final verification output for the user.
