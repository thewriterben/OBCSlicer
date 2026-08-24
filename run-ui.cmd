@echo off
rem OBCSlicer GUI — builds if needed, then serves http://127.0.0.1:8771/
cd /d "%~dp0"
dotnet build src\obcslicer-ui\obcslicer-ui.csproj -c Release --nologo -v q || exit /b 1
dotnet build src\fieldengine\fieldengine.csproj -c Release --nologo -v q
start "" http://127.0.0.1:8771/
dotnet run --project src\obcslicer-ui -c Release --no-build
