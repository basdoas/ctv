# RPM spec for ctv.
#
# ctv is a Native AOT binary, so this spec only repackages the binary that CI
# already compiled (see .github/workflows/release.yml). Nothing is built here.
#
# Local test:
#   mkdir -p ~/rpmbuild/SOURCES && cp ctv-linux-x64.tar.gz ~/rpmbuild/SOURCES/
#   rpmbuild -bb packaging/ctv.spec --define "ctv_version 0.1.0"

%global debug_package %{nil}
%global __strip /bin/true
%global _build_id_links none

Name:           ctv
Version:        %{?ctv_version}%{!?ctv_version:0.0.0}
Release:        1%{?dist}
Summary:        Cool terminal vibe client - multi-provider AI chat and coding agent for the terminal
License:        GPL-3.0-only
URL:            https://github.com/basdoas/ctv
Source0:        ctv-linux-x64.tar.gz
ExclusiveArch:  x86_64

%description
ctv is a terminal client for AI models with a full-screen interface, themes,
in-app settings and an agent that can read and edit files and run commands
after asking for permission. It works with OpenAI, Anthropic (Claude),
Google Gemini, NVIDIA NIM, Mistral, OpenRouter, Groq, Ollama and any
OpenAI-compatible endpoint.

%prep
%setup -q -c

%build
# Nothing to build: the native binary is compiled by CI.

%install
install -D -m 0755 ctv %{buildroot}%{_bindir}/ctv

%files
%license LICENSE
%doc README.md
%{_bindir}/ctv

%changelog
* Mon Nov 09 2026 ctv maintainers <noreply@github.com> - %{version}-1
- Release %{version}
