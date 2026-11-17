
# bash completion for ctv
_ctv() {
    local cur prev opts
    COMPREPLY=()
    cur="${COMP_WORDS[COMP_CWORD]}"
    prev="${COMP_WORDS[COMP_CWORD-1]}"

    opts="/help /login /logout /signout /provider /model /theme /system /config /permissions /save /load /sessions /delete-session /questions /clear /quit /settings /vibe /easter /memories /extensions /auth /artifacts"

    if [[ ${cur} == /* ]] ; then
        COMPREPLY=( $(compgen -W "${opts}" -- ${cur}) )
    elif [[ ${prev} == "/model" || ${prev} == "/provider" || ${prev} == "/theme" || ${prev} == "/load" || ${prev} == "/delete-session" || ${prev} == "/save" ]]; then
        COMPREPLY=( $(compgen -f -- ${cur}) )
    fi

    return 0
}
complete -F _ctv ctv
