# STS2MCP

<p align="center">
  <img src="docs/teaser.png" alt="STS2 MCP" width="90%" />
</p>

STS2MCP lets an AI agent read and play Slay the Spire 2 in solo and co-op runs. Co-op Chat adds in-game messages, quick replies, and party polls for human and AI teammates.

## Choose what to install

| I want to… | Download | Start here |
| --- | --- | --- |
| Chat or vote as a human guest in co-op | **Co-op Chat** | [Player install guide](docs/chat-install.md) |
| Let an AI agent play solo | **MCP** bridge and client | [MCP setup and agent guide](mcp/README.md) |
| Play co-op with an AI teammate | **Co-op Chat** on every seat, plus **MCP** on each AI seat | [Co-op install guide](docs/chat-install.md) and [agent coordination guide](mcp/README.md#combat-coordination) |

Download the ZIPs from Britey's fork. The releases were tested on Windows with game version **v0.113.1**. The MCP bridge lets an authorized local client read and control the game. Co-op automation remains experimental. [GitHub releases](https://github.com/Britey/STS2MCP/releases)

## Reference

- [Agent gameplay tips](AGENTS.md) cover state polling, targeting, and common decisions.
- [Raw HTTP quick reference](docs/raw-simplified.md) and [full HTTP API](docs/raw-full.md) are available for agents that use the bridge directly.

## Build from source

Install the base game and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0). From the repository root, point each project at the game installation.

```powershell
.\build.ps1 -GameDir 'D:/SteamLibrary/steamapps/common/Slay the Spire 2'
dotnet build coordination/STS2_Coordination.csproj -c Release -o out/STS2_Coordination '-p:STS2GameDir=D:/SteamLibrary/steamapps/common/Slay the Spire 2'
```

The bridge DLL is in `out/STS2_MCP/`. Copy it into the game's `mods/STS2_MCP/` folder and copy `mod_manifest.json` there as `STS2_MCP.json`. Copy the chat DLL and `STS2_Coordination.json` from `out/STS2_Coordination/` into `mods/STS2_Coordination/`.

On macOS, use `dotnet build` directly for the bridge. The projects resolve the game assemblies inside `SlayTheSpire2.app`. The mods folder is `SlayTheSpire2.app/Contents/MacOS/mods/`.

```bash
GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
dotnet build STS2_MCP.csproj -c Release -o out/STS2_MCP -p:STS2GameDir="$GAME_DIR"
dotnet build coordination/STS2_Coordination.csproj -c Release -o out/STS2_Coordination -p:STS2GameDir="$GAME_DIR"
```

## License

MIT. This fork is based on [Gennadiyev/STS2MCP](https://github.com/Gennadiyev/STS2MCP).

<details>
<summary>Original project FAQ</summary>

### Why let the AI play the game for me?

I start building this mod with the hope that I can co-op with an AI player. Singleplayer is originally just built for validation.

### You did not answer the question!

First of all, I play lots of games, including service games that has daily/weekly tasks. I really hoped that modern AI could save me from the grind, which, if you have tried one or more of the GUI agents, never really materialized. Let's face it: modern AI is still pretty bad at gaming because no one cares.

About my intention, as a researcher that loves playing games, the purpose of STS2MCP is to test AI models and agents in a rarely explored (we call it out-of-distribution) domain. Ultimately, this might turn into a benchmark for evaluating the reasoning and decision-making capabilities of different language models.

STS2 is just an example to show how good (or bad) current AI agents are at playing such games. **I have no intention to replace human players with AI, and I would definitely rather play STS2 myself** as a big fan of the game.

### Is this a cheat mod?

It can be, but it doesn't have to be. The mod itself does not alter the gameplay. It is just an interface that allows external programs to interact with the game. What you do with that interface is up to you.

### How many tokens do a run consume?

I evaluated on the Ironclad. Claude Sonnet 4.6 uses slightly more than 8M tokens (counting both input, output and tool responses) for a full run. GPT-5.4 averages 7.34M tokens. Depending on your prompt and model choice, it can be more or less.

### Do you have a roadmap for future features?

The project is still too early to have a clear roadmap. My current focus is to make sure the core features are stable and well-documented. However, I am open to suggestions and contributions from the community.

- Solidifying multiplayer features and fixing bugs is a priority
- Add support for in-game communication in multiplayer runs when collaborating with an AI agent
- Self-reflection and learning from past runs to improve future performance
- Benchmarking different models and agents is also on my mind

</details>
