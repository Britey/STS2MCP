# Install and use Co-op Chat

[简体中文](chat-install.zh-CN.md)

Co-op Chat adds in-game messages, quick replies, and group ballots to Slay the Spire 2. This release was tested on Windows with game version **v0.113.1**.

## Install on each player's game

1. Quit Slay the Spire 2. Download **STS2_Coordination-0.2.0.zip**. Everyone in the party should use the same chat mod version. [Co-op Chat 0.2.0](https://github.com/Britey/STS2MCP/releases/tag/chat-v0.2.0)
2. In Steam, right-click **Slay the Spire 2**, then choose **Manage → Browse local files**. Open the `mods` folder there, or create it if needed. On macOS, open **Show Package Contents** on `SlayTheSpire2.app` and use `Contents/MacOS/mods`.
3. Extract the ZIP and copy the whole `STS2_Coordination` folder into `mods`. Check that the folders and files look like this:

   ```text
   mods/
     STS2_Coordination/
       STS2_Coordination.dll
       STS2_Coordination.json
   ```

4. Launch the game and accept the mod loading prompt if it appears. Check for **STS2 Co-op Coordination** in the in-game Mods screen. Start or resume a co-op run. **Chat** should appear in the upper right once a teammate is connected.

Human guests need only Co-op Chat. Each AI-controlled game also needs the MCP mod and its client. A human host who wants to set a shared language needs the MCP mod on the hosting game. [MCP setup](../mcp/README.md#install-and-connect)

## Chat, vote, or reply

- Open **Chat** (**聊天**), choose a teammate, type a message, and press **Send** or **Enter**. Choose **Everyone** (**所有队友**) to message the party when you have multiple teammates.
- When a teammate asks a question, click a reply button. If the prompt offers **Type a reply**, you can write your own answer. If you choose to go first, make your play and use **Go ahead** if the prompt is still waiting.
- In a group poll, each recipient can vote once. Most-votes polls choose the option with the highest tally. If there's a tie, the host gets a second ballot with the tied choices. A poll asking whether *anyone* can do something settles on the first Yes. No requires everyone to vote No.

## Set the party language

On the host's game, open **Settings → General → MCP Language** and select **简体中文** or **English**. The choice applies to built-in prompts and the game terms shown to MCP agents. Choose **Follow Game** to let each seat use its own preference. Type custom messages in the language you want your teammates to read.

If Chat is missing, verify that the game lists **STS2 Co-op Coordination** and that a teammate with the same version has joined the run. After updating, restart the game. For an earlier manual install, move any older copies of the same mod out of the `mods` folder so the game loads one copy.
