# AGENTS.md

Agents working in this repository follow the shared protocol in [elsa-workflows/.github AGENTS.md](https://github.com/elsa-workflows/.github/blob/main/AGENTS.md): no self-reviews, one advisory reviewer, merges by the elsa-4-code-review app after approval on the latest commit plus green CI.

Issue pickup uses [Issue Bus Lite](https://github.com/elsa-workflows/.github/blob/main/AGENTS.md#issue-bus-lite). Pickup query:

```text
repo:valence-works/bpmn is:issue is:open no:assignee label:ready-for-agent -label:blocked -label:"needs:decision" sort:created-asc
```
