# Sourced by the setup scripts. Reads KEY=VALUE files by parsing, never by
# sourcing: setup/.env may be a symlink to a file shared with other tools
# (the selva repo's .env), whose values need not be valid shell.

# The only variables the setup scripts take from the file. deploy-server.sh
# writes exactly these to the server's .env.
COMPUTE_ENV_VARS="RHINO_TOKEN RHINO_COMPUTE_KEY IMAGE_NAME CONTAINER_NAME PORT CHILD_COUNT REPO_URL BRANCH COMPUTE_REF NO_BUILD LOCAL_PLUGINS PLATFORM CPUS CPUSET MEMORY CACHE_LIMIT_PERCENT IDLE_SPAN RESTART BIND"

# env_value FILE NAME → last NAME= value; quotes stripped, unquoted trailing comment dropped
env_value() {
    [ -f "$1" ] || return 0
    tr -d '\r' < "$1" \
        | sed -n "s/^[[:space:]]*\(export[[:space:]][[:space:]]*\)\{0,1\}$2=//p" | tail -1 \
        | sed -e '/^"/{s/^"\([^"]*\)".*$/\1/;b' -e '}' \
              -e "/^'/{s/^'\([^']*\)'.*\$/\1/;b" -e '}' \
              -e 's/[[:space:]]*#.*$//' -e 's/[[:space:]]*$//'
}

# load_env FILE → export each COMPUTE_ENV_VARS entry the shell has not already set
load_env() {
    [ -f "$1" ] || return 0
    local var val
    for var in $COMPUTE_ENV_VARS; do
        [ -z "${!var-}" ] || continue
        val="$(env_value "$1" "$var")"
        [ -z "$val" ] || export "$var=$val"
    done
}
