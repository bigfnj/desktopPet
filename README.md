# labels-data branch — DesktopPet fortune v2 labels

Durable snapshot of `src/Fortunes/labels-store.tsv`: the completed v2
classification of every unique fortune, one row of `text<TAB>topic<TAB>genre`.

- Rows: 56,064 unique fortunes
- Taxonomy: 2026-07-31 (12 topics incl. `health-body`; 12 genres)
- NOT part of any release. On `master` this file is gitignored, and the release
  gate (`packaging/Invoke-ReleaseGate.ps1`) fail-closes if it is ever tracked
  there. This orphan branch exists solely to preserve the labeling work
  off-machine; releases never build from it.
- Re-snapshot after re-labeling: from a clean master worktree, re-hash the store
  and `git branch -f labels-data <new-commit>`, then force-push this branch.

Produced by the fortune labeling pipeline (`src/Fortunes/label-*.sh`).
