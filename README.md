# Spide → Discord Rich Presence

Windows companion app that reads the current Windows media session and publishes track information to Discord Rich Presence.

## Current implementation

- Reads Windows Global System Media Transport Controls (GSMTC).
- Prefers a media session whose source app ID contains `spide`, then falls back to the current system media session.
- Publishes title, artist, play/pause state, and a timestamp-based progress bar.
- Uses a Discord application image asset as the large image.

The supplied Spide desktop executable contains Windows media-session integration symbols, so GSMTC is the first integration path to test. Runtime behavior still needs verification on a Windows machine with Spide playing audio.

## Setup

1. Create a Discord application: https://discord.com/developers/applications
2. Copy its Application ID.
3. In **Rich Presence → Art Assets**, upload a square image and name it `spide` (or change `discordImageKey`).
4. Copy `config.example.json` to `config.json` beside the executable and paste your Application ID.
5. Start the Discord desktop client, start Spide playback, then run `SpideDiscordPresence.exe`.

You can alternatively set the `DISCORD_APP_ID` environment variable.

## Build

Requires Windows and .NET 8 SDK:

```powershell
dotnet publish src/SpideDiscordPresence/SpideDiscordPresence.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

A GitHub Actions workflow builds the Windows x64 executable on each push.

## Artwork note

Discord's standard Rich Presence image field accepts an image asset uploaded to the Discord application, not an arbitrary local thumbnail from another process. This first version therefore uses the configured `spide` asset. Dynamic per-track cover art needs an additional supported image-hosting/Discord display strategy; the media session thumbnail can be read separately, but cannot simply be passed as a local file path to Rich Presence.

## Troubleshooting

- Keep Discord Desktop open; browser Discord does not provide the local RPC endpoint.
- If no Spide track is detected, check whether Windows shows Spide in the media controls. The app logs the detected track when it changes.
- If another app is playing audio, the fallback session may publish that app's track. The source-app matching rule can be tightened once the actual Spide session ID is confirmed.
