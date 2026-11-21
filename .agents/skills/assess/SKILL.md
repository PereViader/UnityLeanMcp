---
name: assess
description: >-
  Assess whether a feature request or proposed change complies with project rules,
  system invariants, and architecture before implementation. Use when the user
  writes a short prompt like "assess <feature>" or asks to evaluate an idea,
  proposal, or request against repository standards.
---

# Assess Feature Request

Perform a fast, read-only architectural and rule-compliance check of a feature
request before any planning or implementation begins.

## Quick Invocation

Users can invoke this skill with minimal prompts:

- `assess <feature request or idea>`
- `assess: <short description>`

If no feature description is provided, ask the user what feature or change to
assess before proceeding.

## Authoritative Project Knowledge (Do Not Duplicate)

Never duplicate or hardcode project rules inside this skill. Always read the
current project knowledge files directly:

1. [AGENTS.md](../../../AGENTS.md) — Repository-wide development rules and constraints.
2. [docs/README.md](../../../docs/README.md) — Architecture overview, intentional
   public catalog boundaries, system invariants, and topic router.
3. Relevant topic guides under `docs/` routed from [docs/README.md](../../../docs/README.md)
   (read only the guides/sections relevant to the request).

## Procedure

This skill is strictly **read-only**. Do not modify files or start implementing.

1. Read `AGENTS.md` and `docs/README.md`.
2. Follow the topic router in `docs/README.md` to read only the `docs/*.md`
   guide(s) affected by the request. Inspect directly affected source files only
   if needed to verify an assumption or potential conflict.
3. Evaluate the request against:
   - **Repository Rules** (`AGENTS.md`): platform portability, transactional
     state, domain-reload resilience, completion semantics, and backward-compatibility
     constraints.
   - **System Invariants & Catalog Scope** (`docs/README.md`): ownership,
     correlation, thread-safety, and intentional public tool boundaries.
   - **Topic Guidance & Pitfalls** (`docs/*.md`): design decisions and known
     failure modes in the affected subsystem(s).
4. Return a concise, structured assessment:
   - **Verdict**: `APPROVE` | `REVISE` | `REJECT`
   - **Rule & Invariant Checks**: Brief bullets citing exact files and sections
     (e.g., `AGENTS.md`, `docs/README.md`, `docs/<guide>.md#<section>`).
   - **Blocking Issues** *(when `REVISE` or `REJECT`)*: Concrete conflicts with
     project rules, invariants, or scope.
   - **Recommended Adjustment** *(when `REVISE` or `REJECT`)*: Smallest compliant
     alternative, or why the request is out of scope.
   - **Open Questions** *(only if needed)*: Ambiguities that block a verdict.
