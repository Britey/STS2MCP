# Co-op regression checks

Build the bridge in Release mode with the game assemblies installed. On Windows, run the comparison checks in PowerShell 7 with the game data directory as the argument.

```powershell
./tests/coop/Verify-SequencePolicy.ps1 -GameDataDir "<game>/data_sts2_windows_x86_64"
```

The live checks exercise real actions on disposable game seats. Install the bridge and co-op chat on each seat and use the Python environment from the MCP client. Start the connected seats in the scenario under test. Set their HTTP ports to 15626 (host), 15627 (guest), and 15628 for a third seat, or pass `--first-port` to the relay. Set each game's profile directory so its bridge writes its token under `<run>/profiles/<seat>/SlayTheSpire2/STS2_MCP.token`. Use a fresh run directory for each scenario.

From the repository root, start the relay, then run the verifier in another terminal with the same directory. Use two seats for shop, event, and treasure. The relay exits when a file named `stop-relay` appears in the run directory.

```sh
python tests/coop/relay.py "<run>" mcp/server.py --seats 3
python tests/coop/verify_recovery.py "<run>" race
```

| Case | Starting position |
|---|---|
| `race` | Three players in round one against a single target that survives the opening strikes. The guest has a plain Strike, the host has a playable attack, and the third seat can play and vote. |
| `treasure-instant`, `treasure-normal` | Two seats at an unclaimed chest with two different relics. Set each seat's speed before launch. Both bid for the first relic. The verifier checks the awards and inventories and times when both seats can proceed. |
| `shop` | Both seats in a shop, each able to buy a card. The verifier checks the receipt, exact deck and gold changes, sold row, chat wait, and map exit. |
| `event` | The guest is in the Tezcatara dialogue before the choice at index 2. The verifier advances the dialogue and chooses that option. |

The verifier writes `result.json` after its assertions pass. The relay records each seat's tool requests and responses locally. Each live case needs its own prepared game state.
