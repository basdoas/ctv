
# zsh completion for ctv
#compdef ctv

_ctv() {
    local -a commands
    commands=(
        "/help:Show help"
        "/login:Add or update a provider"
        "/logout:Sign out from a provider"
        "/provider:Switch active provider"
        "/model:Change model"
        "/theme:Pick a color theme"
        "/system:Edit the system prompt"
        "/config:Show config file and providers"
        "/permissions:View and manage safety allow-list"
        "/save:Save the current conversation"
        "/load:Load a saved conversation"
        "/sessions:List saved conversations"
        "/delete-session:Remove a saved conversation"
        "/questions:Answer questions the AI asked"
        "/clear:Clear conversation and session permissions"
        "/quit:Exit"
        "/settings:Settings and preferences"
        "/vibe:Show ctv logo"
        "/easter:List easter eggs"
        "/memories:Show persistent memories"
        "/extensions:List installed extensions"
        "/auth:Show authentication status"
        "/artifacts:List code blocks from last response"
    )
    _describe -t commands "ctv commands" commands
}
