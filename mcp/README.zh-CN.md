# MCP 安装与智能体指南

[English](README.md) · [联机聊天安装指南](../docs/chat-install.zh-CN.md)

STS2 MCP 由游戏内桥接模组和本地 stdio MCP 服务组成。桥接模组提供游戏状态和操作接口，MCP 服务将其提供给 AI 智能体。此版本已在 Windows 上用《杀戮尖塔 2》**v0.113.1** 测试。

## 安装与连接

1. 退出游戏。从同一发行版下载 **STS2_MCP-0.5.0.zip** 和 **STS2_MCP-client-0.5.0.zip**。[STS2 MCP 0.5.0 下载](https://github.com/Britey/STS2MCP/releases/tag/mcp-v0.5.0)
2. 在 Steam 库中右键单击游戏，选择 **管理 → 浏览本地文件**。解压桥接模组的 ZIP，把整个 `STS2_MCP` 文件夹放进游戏的 `mods` 目录。最终应有 `mods/STS2_MCP/STS2_MCP.dll` 和 `mods/STS2_MCP/STS2_MCP.json`。macOS 的 `mods` 目录位于 `SlayTheSpire2.app/Contents/MacOS/mods/`。
3. 启动游戏。如出现允许加载模组的提示，请确认。打开模组列表，确认有 **STS2 MCP**。
4. 将客户端 ZIP 解压到任意固定目录，例如 `C:/Tools/STS2MCP`。安装 [uv](https://docs.astral.sh/uv/getting-started/installation/) 并使用 Python 3.11 或更高版本。将下列路径换成解压后含有 `server.py` 的 `mcp` 文件夹路径，运行命令以安装锁定版本的依赖并检查启动。

   ```powershell
   uv run --directory "C:/Tools/STS2MCP/mcp" --locked python server.py --help
   ```

5. 在 AI 客户端中注册一个名为 `sts2` 的本地 stdio MCP 服务。如果客户端使用 `mcpServers` JSON 配置，可参考以下示例。

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

   使用你实际的 `mcp` 文件夹绝对路径。客户端找不到 `uv` 时，将 `command` 改为 `uv` 可执行文件的绝对路径。
6. 重新连接 AI 客户端。在游戏主菜单，让智能体调用 `get_game_state(format="json")`，预期返回 `state_type: "menu"`。已经进入联机冒险时，调用 `mp_get_game_state(format="json")`。联机聊天和投票需要每位参与者安装相同版本的[联机聊天模组](../docs/chat-install.zh-CN.md)。

### 连接排查

- 桥接模组默认监听 `localhost:15526`。打开 <http://localhost:15526/>，应看到 `status: "ok"` 和桥接模组版本。如果无法连接，确认游戏在运行、模组已启用，以及清单文件名为 `STS2_MCP.json`。
- 启用模组后，游戏会生成令牌文件。Windows 下默认路径为 `%APPDATA%\SlayTheSpire2\STS2_MCP.token`。智能体无法读取时，找到游戏日志里打印的路径，将 `"--token-file", "令牌文件的绝对路径"` 加到客户端参数末尾。请勿公开令牌。
- 如果在 `STS2_MCP.conf` 中改了 `port`，重启游戏，再将 `"--port", "<端口>"` 加到客户端参数末尾。`--host` 可选 `localhost` 或 `127.0.0.1`。
- HTTP 401 表示令牌校验失败，确认客户端使用的是当前运行实例的令牌。HTTP 409 表示工具与游戏模式不匹配。联机冒险使用 `mp_*` 工具，单人和主菜单使用对应的普通工具。

## 智能体操作流程

1. **先读取状态。** 优先使用 JSON。单人战斗调用 `get_game_state(format="json", compact=true, wait_for_ready=true)`，联机调用 `mp_get_game_state(format="json")`。根据 `state_type` 和当前可用选项选择工具。在主菜单用 `menu_select` 选择已列出的操作。
2. **确认行动条件和目标。** 先看 `battle.can_act`、`battle.warnings`、敌人意图和生效状态，再计划出牌。`battle.can_act` 为 false 时查看 `battle.action_blocked_reason`。用最新的 `card_index`，瞄准敌人时用 `entity_id`，指定联机玩家时用 `target_player_slot`。手牌变化后重新读取索引。
3. **确认操作结果。** 单人出牌或结束回合时可传 `return_state=true`，在回执里获取下一份紧凑状态。战斗动画未结束、操作仍在处理或回执超时时，先刷新状态再决定要不要重试。出现选牌或确认界面时，使用对应工具完成选择。
4. **联机先配合。** 执行依赖队友的操作前，检查收到的提问和消息。需要队友确认时，等回复或投票结束再行动。地图投票后用 `run.map_coord` 确认实际到达位置。查看 `battle.can_undo_end_turn`，判断能否撤销结束回合的投票。[战斗配合](#战斗配合)
5. **报告阻塞。** 如果当前界面需要手动操作，或者无法确认动作是否生效，报告当前界面、最后一个已确认的操作和有关回执。

可给智能体的示例指令

> 请通过 STS2 MCP 工具操作本局。每次行动前读取状态，等游戏就绪。先查看战斗警告，需要配合时先问队友。遇到无法处理的界面时暂停，并报告最后一个确认成功的操作。

## 常用工具

| 场景 | 工具和用法 |
| --- | --- |
| 单人战斗 | `combat_play_card`、`combat_end_turn`。批量出牌用 `combat_play_sequence`，最多 6 张。提供起始手牌里的 `card_index`、`card_id` 和必要的目标。部分完成时读 `submitted` 和 `stop_reason`。 |
| 路线与房间 | `map_choose_node` 选路线，`event_choose_option` 选事件选项，`rest_choose_option` 操作休息处，`shop_purchase` 按当前 `item_index` 购买。商店开着时，`proceed_to_map(leave_shop=true)` 可一次离店。 |
| 奖励与预览 | 用 `rewards_claim` 领取奖励，`rewards_pick_card` 选牌或 `rewards_skip_card` 跳过。用 `bundle_select` 打开预览，再调用 `bundle_confirm_selection` 或 `bundle_cancel_selection`。 |
| 联机战斗 | `mp_combat_play_card`、`mp_combat_end_turn`、`mp_combat_undo_end_turn`。确实要留下可打的牌时，结束回合才传 `allow_playable_cards=true`。 |
| 联机路线与行动 | `mp_map_vote` 投票，`mp_rest_choose_option` 操作休息处，`mp_treasure_claim_relic` 参与遗物竞选。用 `target_player_slots` 确认可以成为目标的队友。 |
| 档案与游戏知识 | `get_profile` 读档案摘要，`get_compendium` 查图鉴和近期记录，`search_wiki` 查已发现的卡牌、遗物或已解锁角色。 |

`battle.warnings` 在该场战斗首次满足条件时报告危险。后续回合应从当前状态读取层数和剩余时间。单人模式的紧凑状态保留手牌、敌人意图和牌堆数量，需要查看牌堆明细时请读取完整状态。

[完整工具表（英文）](README.md#singleplayer) · [玩法建议（英文）](../AGENTS.md) · [HTTP API 参考（英文）](../docs/raw-full.md)

## 战斗配合

| 工具 | 用法 |
| --- | --- |
| `mp_coordination_prompt` | 向一位队友提问，`recipient_slot` 使用 `players` 中从 0 开始的槽位。 |
| `mp_coordination_poll` | 同时向所有已连接的队友发起同一个投票。 |
| `mp_coordination_state` | 查看提问、`outgoing_poll`、聊天消息和队友语言。用 `latest_id` 作为本局下一次读取的 `after_id`。 |
| `mp_coordination_reply` | 按 `prompt_id` 回复。从 0 开始的 `choice_index`、允许时的 `text`、或 `go_ahead=true` 三选一。 |
| `mp_coordination_message` | 用 `recipient_slot` 私聊，或用 `all_players=true` 发给全队。 |
| `mp_coordination_cancel` | 按 ID 撤回自己发起的提问或投票。 |

每个席位同时只能发起一个尚未结束的提问或投票。普通选择题提供 2–4 个选项。内置选项例如 `{"kind":"agent_first"}`、`{"kind":"focus_enemy","target":"NIBBIT_0"}` 和 `{"kind":"no_preference"}`。单独向一位队友提问时，`teammate_first` 会等待该队友出牌或点击「轮到你了」。询问全队出牌顺序时，在自定义选项里写清楚玩家。每轮默认 25 秒，可设为 5–60 秒。

- **按票数决定**。传 `rule="most_votes"`。全员回复或时间到后，得票最多的选项胜出。平票时，由房主从平票选项中选出结果。无人投票、房主未在限时内决定、或原战斗情境变化时，投票失效。
- **有人回答是即可**。传 `rule="any_yes"` 和问题文字，省略 `options`。例如 `question="这回合有人能施加易伤吗？"`。一票「是」立即确认，`outgoing_poll.votes` 会标明谁回答。所有队友明确投「否」才确认没有。无人投「是」又有人未回复时，结果为未知。
- **确认后再行动**。在 `pending`、`waiting_for_teammate` 或 `waiting_for_host` 时暂缓相关操作，间隔一小段时间再读取状态。投票为 `answered` 时读取 `winning_choice_index`。刷新战斗状态后，可把对应 `prompt_id` 或 `poll_id` 作为 `coordination_prompt_id` 传给已商定的出牌、药水或结束回合操作。`expired` 或 `cancelled` 时根据新状态重新判断。

## 使用简体中文

- 房主可以在 **设置 → 常规 → MCP 语言** 中选择 **简体中文** 或 **English**。选择会用于全队的内置提问、回复按钮和 MCP 战斗名称。**跟随游戏** 会取消全队覆盖。设置即时生效，保存到 DLL 旁边的 `STS2_MCP.conf`。若手动编辑，保存后重启游戏。
- 智能体优先用 `party_language` 选择沟通语言。未设置时，私聊参考收件人的 `language`，全队消息参考 `preferred_language`。看到 `zhs` 时用简体中文提问，例如「请先给这个敌人施加易伤」。
- 调用 `mp_get_game_state(language="zh-CN")` 或 `get_game_state(language="zh-CN")` 可单次读取官方中文敌人名、宠物名、增益和减益名。用 `player.status` 查看自己已有的效果，用 `players[].status` 查看队友的效果。直接读得到的信息无需再发投票。
- 其他术语可用 `get_simplified_chinese_names` 查询，例如传入 `{"kind":"card","id":"BASH"}` 或 `{"kind":"power","id":"VULNERABLE_POWER"}`，后者返回 **易伤**。发送消息使用中文名，向游戏提交操作仍使用工具要求的 ID。
