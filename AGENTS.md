# JL Engine Agent Instructions

## Protected Product Behavior

JL Engine is intentionally a machine-execution engine. Its ability to run shell
commands, execute code, read and write files, automate browsers, use external
integrations, and forge runtime tools is core product behavior.

Do not remove, disable, weaken, sandbox away, replace, or silently override
these capabilities because they appear powerful or risky during routine code
work. Do not change their execution permissions or policy boundaries without
the user's explicit direction.

The project is intended to present a clear disclaimer and obtain informed user
consent before machine-level execution. Preserve that model. Changes to the
disclaimer, approval flow, session identity, tunnel authentication, capability
exposure, or audit visibility must be called out explicitly to the user.

Security and reliability work should make consent and the execution boundary
clear, dependable, and observable. It should not turn JL Engine into a
non-executing assistant.

## Change Discipline

- Treat execution tools and their tests as intentional behavior, not dead code.
- Prefer focused fixes over broad safety-driven rewrites.
- Do not delete or rewrite engine capabilities without first explaining the
  behavioral impact and receiving explicit approval.
- Keep generated runtime state, local credentials, databases, logs, and build
  output out of version control unless the user specifically requests otherwise.
- When a change affects execution authority, report exactly what changed and
  what validation was performed.