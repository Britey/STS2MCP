# MCP setup and agent guide

[简体中文](README.zh-CN.md)

The MCP release pairs an in-game bridge with a local stdio server. The bridge exposes game state and actions to the server, which presents them as MCP tools. The release was tested on Windows with Slay the Spire 2 **v0.113.1**.

## Install and connect

1. Quit the game. Download both **STS2_MCP-0.5.0.zip** and **STS2_MCP-client-0.5.0.zip**. [STS2 MCP 0.5.0](https://github.com/Britey/STS2MCP/releases/tag/mcp-v0.5.0)
2. In Steam, right-click the game and select **Manage → Browse local files**. Extract `STS2_MCP-0.5.0.zip` and place its `STS2_MCP` folder inside the game's `mods` folder. Create `mods` if needed. Keep `STS2_MCP.dll` and `STS2_MCP.json` together as `mods/STS2_MCP/STS2_MCP.dll` and `mods/STS2_MCP/STS2_MCP.json`. On macOS, the mods directory is inside `SlayTheSpire2.app/Contents/MacOS/mods/`.
3. Launch the game and accept the mod loading prompt if it appears. Check that **STS2 MCP** appears in the in-game Mods screen.
4. Extract the client ZIP to a permanent folder such as `C:/Tools/STS2MCP`, with `mcp/server.py`, `mcp/pyproject.toml`, and `mcp/uv.lock` inside. Install [uv](https://docs.astral.sh/uv/getting-started/installation/) and use Python 3.11 or newer. This checks the launch command and installs the locked dependencies:

   ```powershell
   uv run --directory "C:/Tools/STS2MCP/mcp" --locked python server.py --help
   ```

5. Register `sts2` as a local stdio MCP server in your AI client. For clients that use an `mcpServers` JSON config, use:

   ```json
   {
     "mcpServers": {
       "sts2": {
         "command": "uv",
         "args": ["run", "--directory", "C:/Tools/STS2MCP/mcp", "--locked", "python", "server.py"]
       }
     }
   }
   ```

   Replace the example directory with the absolute path to your extracted `mcp` folder. If the AI client cannot find `uv`, use its absolute executable path as `command`.
6. Reconnect the AI client and ask it to call `get_game_state(format="json")` from the game's main menu. Expect a result with `state_type: "menu"`. If you're already in co-op, use `mp_get_game_state(format="json")`. For co-op chat and polls, install the [Co-op Chat companion](../docs/chat-install.md) on each seat.

### Connection checks

- The game mod listens on `localhost:15526` by default. Visiting `http://localhost:15526/` should show `status: "ok"` and the bridge version. If the connection is refused, verify that the game is running, the mod is enabled, and the manifest is named `STS2_MCP.json`.
- The token file is created on first launch with the mod enabled. Its default Windows path is `%APPDATA%\SlayTheSpire2\STS2_MCP.token`. If the agent cannot read it, use the path from the game log and append `"--token-file", "absolute/path/to/STS2_MCP.token"` to the client arguments. Keep the token file private.
- If you set a custom `port` in `STS2_MCP.conf`, restart the game and append `"--port", "<port>"` to the args. The optional `--host` flag accepts `localhost` or `127.0.0.1`.
- HTTP 401 means the bridge rejected the token. Confirm the client uses the token for the running game. HTTP 409 means the run mode and tool family differ. Use `mp_*` state and action tools during co-op and ordinary gameplay tools for solo play or menus.

## Agent playbook

1. **Read before acting.** Start with JSON. Use `get_game_state(format="json", compact=true, wait_for_ready=true)` for solo combat or `mp_get_game_state(format="json")` for co-op. Choose tools according to `state_type` and the options actually shown. Use `menu_select` for advertised menu options before the run begins.
2. **Check readiness and targets.** In a fight, read `battle.can_act`, `battle.warnings`, the enemy intents, and all active statuses before planning. Use the current `card_index` and target `entity_id`, even when the display name is translated. Use `target_player_slot` for a co-op card or potion aimed at a player. Refresh state after a hand change before reusing an index.
3. **Resolve the outcome.** In solo combat, use `return_state=true` for individual plays, or `combat_play_sequence` for a short plan based on the starting hand. Read the submitted prefix and resulting state. If the game is animating, an action is pending, or a receipt times out, refresh state before retrying a mutation. If a selection opens, resolve it with the relevant selection tool.
4. **Coordinate co-op moves.** Check incoming prompts and messages before committing a move that affects teammates. If you're waiting for consent, keep the move on hold until the prompt or poll is resolved. Use `run.map_coord` to verify the actual map destination after voting and `battle.can_undo_end_turn` to tell whether your end-turn vote can be reversed. [Combat coordination](#combat-coordination)
5. **Report blockers.** If a screen needs manual input or you cannot confirm that an action took effect, report the screen and action receipt. Proceed to the next decision only after you can verify the state.

Example instruction to give your agent:

> Play this run through the STS2 MCP tools. Read the current state and wait for readiness before acting. Check warnings and coordinate with the party before dependent plays. Stop at a screen you cannot handle and report the last confirmed action.

[Gameplay strategy and more tool tips](../AGENTS.md) · [Raw HTTP API](../docs/raw-full.md)

## Singleplayer

| Tool | Scope | Description |
|---|---|---|
| `get_game_state(format?, compact?, wait_for_ready?, language?)` | General | Get current game state with optional transition waiting, compact output, and combat-name locale |
| `menu_select(option, seed?)` | General | Select a visible menu/game-over option |
| `get_profile()` | Profiles | Get active profile progress |
| `get_compendium()` | Profiles | Get active profile progress grouped like the in-game Compendium |
| `search_wiki(query, item_type?, limit?)` | Profiles | Look up discovered cards and relics, or unlocked characters |
| `get_simplified_chinese_names(entries)` | Localization | Look up official Simplified Chinese names for game IDs and live enemy IDs |
| `list_profiles()` | Profiles | List profile slots and active slot |
| `switch_profile(profile_id)` | Profiles | Switch to a profile slot through the game UI |
| `delete_profile(profile_id)` | Profiles | Delete an inactive profile slot when `allow_profile_delete` is enabled in the mod config |
| `use_potion(slot, target?)` | General | Use a potion (works in and out of combat) |
| `discard_potion(slot)` | General | Discard a potion to free up the slot |
| `proceed_to_map(leave_shop?)` | General | Proceed to the map, or leave an open shop in one call with `leave_shop=true` |
| `combat_play_card(card_index, target?, return_state?)` | Combat | Play a card from hand and optionally return the next state |
| `combat_play_sequence(plays, end_turn?)` | Combat | Resolve up to six planned cards and return the resulting state |
| `combat_end_turn(return_state?)` | Combat | End the current turn and optionally return the next state |
| `combat_select_card(card_index)` | Combat Selection | Select a card from hand during exhaust/discard prompts |
| `combat_confirm_selection(return_state?)` | Combat Selection | Confirm the in-combat card selection and optionally wait for a prompt or ready state |
| `rewards_claim(reward_index)` | Rewards | Claim a reward from the post-combat screen |
| `rewards_pick_card(card_index)` | Rewards | Select a card from the card reward screen |
| `rewards_choose_alternative(alternative_id)` | Rewards | Choose an enabled native alternative by the exact ID in state, such as `REROLL` or `SACRIFICE` |
| `rewards_skip_card()` | Rewards | Skip the card reward |
| `map_choose_node(node_index, return_state?)` | Map | Choose a map node and optionally wait for the destination |
| `rest_choose_option(option_index)` | Rest Site | Choose a rest site option (rest, smith, etc.) |
| `shop_purchase(item_index)` | Shop | Purchase an item from the shop |
| `event_choose_option(option_index)` | Event | Choose an event option (including Proceed) |
| `event_advance_dialogue()` | Event | Advance ancient event dialogue |
| `deck_select_card(card_index)` | Card Select | Pick/toggle a card in the selection screen |
| `deck_confirm_selection()` | Card Select | Confirm the current card selection |
| `deck_cancel_selection()` | Card Select | Cancel/skip card selection |
| `bundle_select(bundle_index)` | Bundle Select | Open a bundle preview |
| `bundle_confirm_selection()` | Bundle Select | Confirm the current bundle preview |
| `bundle_cancel_selection()` | Bundle Select | Cancel the current bundle preview |
| `relic_select(relic_index)` | Relic Select | Choose a relic from the selection screen |
| `relic_skip()` | Relic Select | Skip relic selection |
| `treasure_claim_relic(relic_index)` | Treasure | Claim a relic from the treasure chest |
| `crystal_sphere_set_tool(tool)` | Crystal Sphere | Switch the active divination tool |
| `crystal_sphere_click_cell(x, y)` | Crystal Sphere | Click a hidden cell in the grid |
| `crystal_sphere_proceed()` | Crystal Sphere | Continue after the minigame finishes |

### Action Readiness

For single-player runs, use `get_game_state(compact=true, wait_for_ready=true)` to wait through combat and treasure transitions in one call. Compact state keeps the hand, enemy intents, resources, and pile counts. Request full state when a decision depends on individual pile contents.

The mod also exposes the game's Instant Mode under **Settings → General**. Enable it on the profile you're using to shorten in-game animations during MCP play.

Use `return_state=true` on `combat_play_card` and `combat_end_turn` to get a compact JSON `state` alongside the action receipt. That state can be the next playable turn, a selection prompt, or a different screen. A wait lasts up to seven seconds. If the receipt includes `state_wait_timed_out` or `state_error`, refresh state before continuing. The receipt's `status: ok` still means the action was accepted.

`map_choose_node` and `combat_confirm_selection` also accept `return_state=true`. The former waits for the destination, and the latter waits for the confirmed choice to settle or a new prompt to open.

`combat_play_sequence` accepts plays in order, each with a `card_index` and `card_id` from the **starting hand**, and an enemy `target` when required. It tracks those cards as indices shift. A stop returns `stop_reason`, the `submitted` prefix of accepted card IDs, and the current state. A submitted card may still be resolving or waiting for your input. Set `end_turn=true` only when you intend to end the original turn after the complete sequence. Check `end_turn_submitted` and the resulting state before acting again.

`battle.warnings` flags each hazard the first time its warning condition is met in an encounter. This includes delayed damage after a kill, card and gold theft, limits on card plays, damage caps, and potentially lethal damage outside the normal attack intents. Read the live statuses for current amounts on subsequent turns. A warning with `interrupt_sequence: true` can stop a planned sequence when first discovered. Planning notices also appear in the response and leave the sequence running.

In combat, check `battle.can_act` before playing a card, using a potion, or ending the turn. When it is false, `battle.action_blocked_reason` explains what is pending. Use the selection tools during a card-selection prompt. After a retryable error, refresh state and use the latest card index.

In treasure rooms, wait for `treasure.can_claim`, `treasure.can_skip`, or `treasure.can_proceed` to match the intended action. With a shop inventory open, the default Proceed closes it. Set `leave_shop=true` to close it and leave in one call.

### Profile Tools

`get_profile()` returns the raw active-profile progress summary: character totals, global totals, discoveries, achievements, epochs, and aggregate stats.

`get_compendium()` presents the same profile data in the shape agents usually need when reasoning about long-term progress. It groups the response into `card_library`, `relic_collection`, `potion_lab`, `bestiary`, `character_stats`, and `run_history`, matching the high-level Compendium cards in the game UI. It also includes `current_run` while a run is active, with a derived `run_id` in `{save_scope}:profile{profile_id}:{start_time}` format.

The Compendium response is profile-scoped, not run-state-scoped. It works from the main menu, does not require navigating the in-game Compendium UI, and summarizes only the 20 most recent saved run-history files to keep the MCP response bounded.

`search_wiki(query, item_type="all", limit=10)` fuzzy-matches names and IDs across the active profile’s discovered cards and relics. Card matches include `base` and `upgraded` blocks with rules text, costs and keywords. Request `item_type="character"` and a name or ID like `REGENT` for an unlocked character’s localized description, starting HP, gold, energy, deck and relics. Use the query `character` with that type to list the unlocked roster. Results default to 10 and are capped at 50.

## Multiplayer

All multiplayer tools are prefixed with `mp_`. They route through `/api/v1/multiplayer` and are only available during multiplayer (co-op) runs. The endpoints automatically guard against cross-mode calls.

| Tool | Scope | Description |
|---|---|---|
| `mp_get_game_state(format?, language?)` | General | Get multiplayer game state (players, votes, bids) with optional combat-name locale |
| `mp_combat_play_card(card_index, target?, coordination_prompt_id?, target_player_slot?)` | Combat | Play a card. Use a party `player_slot` for player-targeted cards. |
| `mp_combat_end_turn(allow_playable_cards?, coordination_prompt_id?)` | Combat | Submit an end-turn vote. Set `allow_playable_cards=true` only to deliberately leave playable cards in hand. |
| `mp_combat_undo_end_turn()` | Combat | Retract an end-turn vote while `battle.can_undo_end_turn` is true |
| `mp_use_potion(slot, target?, coordination_prompt_id?, target_player_slot?)` | General | Use a potion. Potions with player targeting accept a party `player_slot`. |
| `mp_discard_potion(slot)` | General | Discard a potion from the local player's slots |
| `mp_proceed_to_map(leave_shop?)` | General | Proceed to the map, or leave an open shop in one call with `leave_shop=true` |
| `mp_map_vote(node_index)` | Map | Submit a vote, or ping by re-selecting your choice. Check `run.map_coord` and `run.is_traveling` for the resolved destination. |
| `mp_event_choose_option(option_index)` | Event | Vote for / choose an event option |
| `mp_event_advance_dialogue()` | Event | Advance ancient event dialogue |
| `mp_rest_choose_option(option_index, target_player_slot?)` | Rest Site | Choose a rest site option. For Mend, choose a teammate from `target_player_slots`. |
| `mp_shop_purchase(item_index)` | Shop | Purchase an item (per-player inventory) |
| `mp_rewards_claim(reward_index)` | Rewards | Claim a post-combat reward |
| `mp_rewards_pick_card(card_index)` | Rewards | Select a card from the card reward screen |
| `mp_rewards_choose_alternative(alternative_id)` | Rewards | Choose an enabled native alternative by the exact ID in state |
| `mp_rewards_skip_card()` | Rewards | Skip the card reward |
| `mp_deck_select_card(card_index)` | Card Select | Pick/toggle a card in the selection screen |
| `mp_deck_confirm_selection()` | Card Select | Confirm the current card selection |
| `mp_deck_cancel_selection()` | Card Select | Cancel/skip card selection |
| `mp_bundle_select(bundle_index)` | Bundle Select | Open a bundle preview |
| `mp_bundle_confirm_selection()` | Bundle Select | Confirm the current bundle preview |
| `mp_bundle_cancel_selection()` | Bundle Select | Cancel the current bundle preview |
| `mp_combat_select_card(card_index)` | Combat Selection | Select a card during in-combat selection prompts |
| `mp_combat_confirm_selection()` | Combat Selection | Confirm in-combat card selection |
| `mp_relic_select(relic_index)` | Relic Select | Choose a relic from the selection screen |
| `mp_relic_skip()` | Relic Select | Skip relic selection |
| `mp_treasure_claim_relic(relic_index)` | Treasure | Bid on a relic (relic fight if contested) |
| `mp_crystal_sphere_set_tool(tool)` | Crystal Sphere | Switch the active divination tool |
| `mp_crystal_sphere_click_cell(x, y)` | Crystal Sphere | Click a hidden cell in the grid |
| `mp_crystal_sphere_proceed()` | Crystal Sphere | Continue after the minigame finishes |

### Combat Coordination

Install the same version of the Co-op Chat companion on every seat. Each AI seat also needs the MCP bridge and client. A human host needs the MCP bridge to set the party's MCP language. [Player install guide](../docs/chat-install.md)

| Tool | Description |
|---|---|
| `mp_coordination_prompt(recipient_slot, options, question?, allow_text?, lifetime_seconds?)` | Ask one teammate a combat question. Use their zero-based slot in the multiplayer state's `players` array. |
| `mp_coordination_poll(options?, question?, lifetime_seconds?, rule?)` | Ask every connected teammate to vote on the same combat question. |
| `mp_coordination_state(after_id?)` | Read prompts, the outgoing poll, recent chat, and teammate languages by player slot. Use `latest_id` as the next `after_id` within the run. |
| `mp_coordination_reply(prompt_id, choice_index?, text?, go_ahead?)` | Submit one reply using a zero-based choice index, plain text, or `go_ahead=true`. |
| `mp_coordination_message(text, recipient_slot?, all_players?)` | Send chat to one slot or set `all_players=true` to message everyone. |
| `mp_coordination_cancel(prompt_id)` | Withdraw your outgoing prompt or poll by its ID. |

Each seat can have one active outgoing prompt or poll. Give it two to four options such as `{"kind":"agent_first"}`, `{"kind":"focus_enemy","target":"NIBBIT_0"}`, or `{"kind":"no_preference"}`. The `teammate_first` choice in a direct prompt waits for that teammate to finish a card play or choose **Go ahead**. For group turn order, use custom choices with named players. A custom `choice` needs a label. Each ballot round defaults to 25 seconds and accepts 5–60 seconds. An accepted answer stays valid until its combat context changes. Chat is available through the **Everyone** recipient in parties with multiple teammates.

With `rule="most_votes"`, everyone gets one vote and the highest tally wins when all respond or the voting window ends. A tie gives only the host a fresh ballot containing the tied options. If the host started the poll, their tie ballot appears on the same seat. Zero votes, a missing host decision, or a change in combat context expires the poll.

Use `rule="any_yes"` for questions such as “Can anyone apply Vulnerable this turn?” Supply a question and omit options to get Yes and No buttons. One Yes settles the poll immediately, and `outgoing_poll.votes` identifies its player slot. Every teammate must vote No to settle the poll as No. Silence leaves the result unknown when the ballot expires.

Use `outgoing_poll.winning_choice_index` when its status is `answered`, and pass its `poll_id` as `coordination_prompt_id` to bind the gameplay action.

Omit the question and built-in labels when the default wording fits. The host's shared language takes precedence when configured. Use `party_language` for custom text when present. Otherwise, use the recipient's `language` for a direct message or `preferred_language` for party-wide text. If the language is `zhs`, use official terms from `mp_get_game_state(language="zh-CN")` or look them up with `get_simplified_chinese_names`. Pass entries such as `{"kind":"card","id":"BASH"}` and `{"kind":"enemy","id":"NIBBIT_0"}`. Keep the original IDs for tool actions.

Set **Settings → General → MCP Language** to **Follow Game**, **English**, or **简体中文**. The MCP bridge saves the choice as `"language": "game"`, `"language": "en"`, or `"language": "zh-CN"` in `STS2_MCP.conf` beside its DLL. Settings changes take effect immediately. Restart the game after editing the file by hand.

With the co-op companion installed, the host's **English** or **简体中文** selection applies to every connected seat's MCP combat names, built-in prompt text, and suggested language for AI chat. The selected locale is available as `party_language` and `preferred_language` in `mp_coordination_state()`. **Follow Game** clears the host's shared preference, so individual MCP and game language settings take effect again.

Use `mp_get_game_state(language="zh-CN")` or `get_game_state(language="zh-CN")` to request official Simplified Chinese names for enemies, pets, buffs, and debuffs in JSON or Markdown for a single read. In co-op combat, check `player.status` for your own effects and `players[].status` for teammates' effects. For a custom team poll about applying 易伤, pass `rule="any_yes"` and `question="这回合有人能施加易伤吗？"`. Custom questions and chat are sent as written. With **Follow Game**, built-in button labels follow each recipient's game language.

Hold the affected combat action while a direct prompt is `pending` or `waiting_for_teammate`, or a group poll is `pending` or `waiting_for_host`. Poll the coordination state with a brief delay between reads. When the answer arrives, refresh the game state and pass the direct prompt's `prompt_id` or group poll's `poll_id` as `coordination_prompt_id` on the agreed card, potion, or end-turn action. On `expired` or `cancelled`, reconsider the move from fresh game state.
