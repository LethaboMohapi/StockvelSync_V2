#!/bin/sh
set -eu
dotnet StokvelSync.Web.dll --migrate
exec dotnet StokvelSync.Web.dll "$@"
