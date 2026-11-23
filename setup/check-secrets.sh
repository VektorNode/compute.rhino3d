#!/usr/bin/env bash
# Fails when the repository tracks a secret: a file type that holds secrets (Terraform state and
# variables, .env, private keys) or a filled-in Rhino token or Compute key. Prints where, never the
# value. This repository is public, and a pushed secret stays in its history.
#
#   setup/check-secrets.sh            # the tracked tree (CI)
#   setup/check-secrets.sh --staged   # what the next commit would contain (pre-commit hook)
#
# Templates (*.example, *.sample) are allowed; their values are placeholders.

set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

if [ "${1:-}" = "--staged" ]; then
    list() { git diff --cached --name-only --diff-filter=ACMR; }
    grep_() { git grep --cached -nIE "$@"; }
else
    list() { git ls-files; }
    grep_() { git grep -nIE "$@"; }
fi

failed=0

files="$(list | grep -iE '(^|/)(\.env(\.[^/]*)?|[^/]*\.tfstate(\.[^/]*)?|[^/]*\.tfvars|[^/]*\.(pem|key|pfx|p12)|id_(rsa|ed25519|ecdsa)[^/]*)$|(^|/)\.terraform/' \
    | grep -viE '\.(example|sample)$' || true)"
if [ -n "$files" ]; then
    echo "Tracked files that hold secrets (remove them and keep them ignored):" >&2
    echo "$files" | sed 's/^/  /' >&2
    failed=1
fi

# A token or key written out in full: 16+ key characters. Placeholders (your-…, changeme, <…>),
# ${VAR} expansions and an all-caps value (a constant holding the variable's own name) pass.
names='RHINO_TOKEN|RHINO_COMPUTE_KEY|SERVER_RHINO_COMPUTE_KEY|rhino_token|compute_key|api_key|apikey'
hits="$(grep_ "(^|[^A-Za-z_])($names)[\"']?[[:space:]]*[:=][[:space:]]*[\"']?[A-Za-z0-9+/._=-]{16,}" -- \
    ':!*.example' ':!*.sample' ':!setup/check-secrets.sh' 2>/dev/null \
    | awk -v names="$names" '{
        line = $0
        sub(/^[^:]*:[0-9]+:/, "", line)
        if (!match(line, "(" names ")[\"\x27]?[[:space:]]*[:=][[:space:]]*[\"\x27]?[A-Za-z0-9+/._=-]+")) next
        value = substr(line, RSTART, RLENGTH)
        sub(/^[^:=]*[:=][[:space:]]*[\"\x27]?-?/, "", value)
        if (value ~ /^(your|changeme|example|placeholder|xxx)/ || value ~ /^[A-Z0-9_]+$/) next
        split($0, at, ":"); print at[1] ":" at[2]
    }' || true)"
if [ -n "$hits" ]; then
    echo "A Rhino token or Compute key written out in full (move it to an ignored .env):" >&2
    echo "$hits" | sed 's/^/  /' >&2
    failed=1
fi

[ "$failed" = 0 ] && echo "No tracked secrets."
exit "$failed"
