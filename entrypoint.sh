#!/bin/sh
set -eu
dotnet TeamReminder.Server.dll --migrate
exec dotnet TeamReminder.Server.dll
