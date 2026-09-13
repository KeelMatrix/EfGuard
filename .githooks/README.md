# Commit checks

This directory contains repository hooks that keep commit history consistent with the public repository's authorship policy.

## Enable the checks

Run `git config core.hooksPath .githooks` once per clone to enable the repository's local commit checks.

The versioned checks reject attribution trailers, tool-generated attribution, issue references, and merge-conflict markers in new commit messages. The public repository workflow checks the commits introduced by each push or pull request.
