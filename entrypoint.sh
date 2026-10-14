#!/bin/sh
set -eu
export ASPNETCORE_HTTP_PORTS="${PORT:-${ASPNETCORE_HTTP_PORTS:-8080}}"
dotnet TeamReminder.Server.dll --migrate
exec dotnet TeamReminder.Server.dll
