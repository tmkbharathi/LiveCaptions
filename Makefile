SHELL := cmd.exe

# Live Transcription App Makefile

APP_NAME=LiveTranscriptionApp.exe

.PHONY: all build run clean restore kill install-gstreamer

all: build

install-gstreamer:
	winget install gstreamerproject.gstreamer

build:
	@dotnet build -r win-x64

run:
	@taskkill /F /IM $(APP_NAME) /T 2>NUL || (exit 0)
	dotnet run --no-build -r win-x64

restore:
	@dotnet restore -r win-x64

kill:
	@taskkill /F /IM $(APP_NAME) /T 2>NUL || (exit 0)
	@taskkill /F /IM dotnet.exe /T 2>NUL || (exit 0)
	@taskkill /F /IM MSBuild.exe /T 2>NUL || (exit 0)
	dotnet build-server shutdown

clean: kill
	-@dotnet clean
	-@if exist bin rd /s /q bin
	-@if exist obj rd /s /q obj

full-rebuild: clean restore build run
