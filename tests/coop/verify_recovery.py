"""Exercise the co-op actions through persistent MCP test seats on live game clients."""
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import sys
import time
from uuid import uuid4

run = Path(sys.argv[1])
case = sys.argv[2]
report = {}
seats = ["host", "guest", "guest2"] if case == "race" else ["host", "guest"]


def call(seat, tool, **args):
    key = uuid4().hex
    inbox = run / seat
    tmp = inbox / (key + ".pending")
    tmp.write_text(json.dumps(dict(name=tool, args=args, observer=True,
                                   requested=time.time_ns() // 1_000_000)), encoding="utf-8")
    tmp.rename(inbox / (key + ".request"))
    deadline = time.monotonic() + 45
    reply = inbox / (key + ".response")
    while not reply.exists():
        if time.monotonic() > deadline:
            raise TimeoutError((seat, tool))
        time.sleep(.005)
    result = json.loads(reply.read_text(encoding="utf-8"))
    assert not result["error"], (tool, result)
    return result["result"]


def state(seat):
    return call(seat, "mp_get_game_state", format="json")


def combat(seat):
    return call(seat, "mp_combat_decision", wait_seconds=3)


def until(get, check, seconds=15):
    deadline = time.monotonic() + seconds
    while True:
        value = get()
        if check(value):
            return value
        if time.monotonic() >= deadline:
            raise AssertionError(str(value)[:400])
        time.sleep(.035)


def treasure(baseline):
    opening = {s: until(lambda: state(s), lambda v: v.get("treasure", {}).get("can_claim")) for s in seats}
    relic = opening["host"]["treasure"]["relics"][0]["id"]
    for s in seats:
        receipt = call(s, "mp_treasure_claim_relic", relic_index=0)
        assert receipt["status"] == "ok" and (baseline or receipt["choice_status"] == "bid_submitted")
    start = time.monotonic()
    completed = {s: until(lambda: state(s), lambda v: v["treasure"]["can_proceed"]) for s in seats}
    report["receipt_to_both_ready_s"] = round(time.monotonic() - start, 3)
    won = []
    for s in seats:
        def inventory(snapshot):
            return Counter(x["id"] for x in snapshot["player"]["relics"])
        added = list((inventory(completed[s])-inventory(opening[s])).elements())
        assert len(added) == 1, (s, added)
        won.extend(added)
        if not baseline:
            awards = completed[s]["treasure"]["awards"]
            assert any(a.get("player_slot") == seats.index(s) and a.get("relic_id") == added[0] for a in awards), awards
            assert completed["host"]["treasure"]["awards"] == awards
    assert won.count(relic) == 1 and len(set(won)) == 2
    report["awards_in_inventories"] = won
    for s in seats:
        assert call(s, "mp_proceed_to_map")["status"] == "ok"
    for s in seats:
        until(lambda: state(s), lambda v: v["state_type"] == "map")


def chat():
    cursor = call("host", "mp_coordination_state")["latest_id"]
    quiet = call("host", "mp_coordination_state", after_id=cursor, wait_seconds=1)
    assert quiet.get("state_wait_timed_out"), quiet
    with ThreadPoolExecutor() as executor:
        future = executor.submit(call, "host", "mp_coordination_state", after_id=cursor, wait_seconds=5)
        time.sleep(.2)
        assert not future.done()
        assert call("guest", "mp_coordination_message", all_players=True, text="Test purchase complete.")["status"] == "ok"
        wake = future.result()
    assert wake["latest_id"] > cursor and not wake.get("state_wait_timed_out")
    quiet = call("host", "mp_coordination_state", wait_seconds=1)
    assert quiet.get("state_wait_timed_out") and quiet["latest_id"] == wake["latest_id"], quiet
    text = "Ready to leave the shop."
    assert call("guest", "mp_coordination_message", all_players=True, text=text)["status"] == "ok"
    until(lambda: call("host", "mp_get_game_state", coordination_after_id=wake["latest_id"]), lambda md: text in md)
    assert call("host", "mp_coordination_state", wait_seconds=1).get("state_wait_timed_out")


def poll_without_chat():
    cursor = call("guest", "mp_coordination_state")["latest_id"]
    with ThreadPoolExecutor() as executor:
        waiting = executor.submit(call, "guest", "mp_coordination_state", after_id=cursor, wait_seconds=8)
        poll = call("host", "mp_coordination_poll", rule="any_yes", question="Ready to start?")
        poll_id = poll["outgoing_poll"]["poll_id"]
        incoming = waiting.result()
        assert not incoming.get("state_wait_timed_out") and incoming["incoming_prompt"]["poll_id"] == poll_id
        cursor = call("host", "mp_coordination_state")["latest_id"]
        waiting = executor.submit(call, "host", "mp_coordination_state", after_id=cursor, wait_seconds=8)
        assert call("guest", "mp_coordination_reply", prompt_id=incoming["incoming_prompt"]["prompt_id"],
                    choice_index=0)["status"] == "ok"
        answered = waiting.result()
        assert not answered.get("state_wait_timed_out") and answered["outgoing_poll"]["status"] == "answered"


def shop():
    catalog = call("host", "list")
    assert "item_index" in catalog["mp_shop_purchase"]["inputSchema"]["required"]
    assert "slot" in catalog["mp_use_potion"]["inputSchema"]["required"]
    for s in seats:
        opening = state(s)
        item = next(i for i in opening["shop"]["items"] if i["category"] == "card" and i["can_afford"])
        receipt = call(s, "mp_shop_purchase", item_index=item["index"])
        assert receipt["item_name"] == item["card_name"] and receipt["price"] == item["price"] and receipt["item_index"] == item["index"]
        current = until(lambda: state(s), lambda v: v["player"]["gold"] == opening["player"]["gold"] - item["price"])
        before_deck = Counter(c["id"] for c in opening["player"]["deck"])
        after_deck = Counter(c["id"] for c in current["player"]["deck"])
        assert after_deck == before_deck + Counter({item["card_id"]: 1}), (s, item["card_id"])
        sold = current["shop"]["items"][item["index"]]
        assert not sold["is_stocked"] and sold["sold_name"] == item["card_name"]
    chat()
    for s in seats:
        assert call(s, "mp_proceed_to_map", leave_shop=True)["status"] == "ok"
        until(lambda: state(s), lambda v: v["state_type"] == "map")


def event():
    dialogue = state("guest")["event"]
    advances = 0
    while dialogue["can_advance_dialogue"] and advances < 8:
        result = call("guest", "mp_event_advance_dialogue")
        assert result["status"] == "ok" and result["event"] != dialogue
        dialogue = result["event"]
        advances += 1
    assert advances and any(o["can_choose"] for o in dialogue["options"]), dialogue
    report["clicks_until_choice"] = advances
    assert call("guest", "mp_event_choose_option", option_index=2)["status"] == "ok"


def race():
    until(lambda: combat("host"), lambda v: v.get("battle", {}).get("can_act"))
    poll_without_chat()
    first = {s: combat(s) for s in seats}
    assert all(v["player_count"] == 3 for v in first.values())
    for tool, params in (("mp_combat_play_card", {"card_index": 999}), ("mp_use_potion", {"slot": 999})):
        rejected = call("host", tool, **params)
        assert rejected["status"] == "error" and rejected["state"]["battle"]["can_act"]
        assert rejected["next_tool"] == tool, rejected
    # This fixture has a pure basic attack on seat one. Hold the host's original
    # observation and exercise strict, out-of-range and in-range submission.
    strike = next(c for c in first["guest"]["player"]["hand"] if c["id"].startswith("STRIKE_") and c["can_play"])
    target = strike["legal_enemy_ids"][0]
    start = first["host"]["battle"]["enemies"][0]["hp"]
    assert call("guest", "mp_combat_play_card", card_index=strike["index"], target=target, return_state=True)["status"] == "ok"
    after = until(lambda: combat("host"), lambda v: v["battle"]["can_act"] and v["battle"]["enemies"][0]["hp"] < start)
    hp = after["battle"]["enemies"][0]["hp"]
    card = next(c for c in first["host"]["player"]["hand"] if target in c.get("legal_enemy_ids", []) and c["can_play"])
    untargeted = next(c for c in first["host"]["player"]["hand"] if c["target_type"] == "Self" and c["can_play"])
    rejected = call("host", "mp_combat_play_sequence",
                    turn_id=first["host"]["battle"]["sequence_turn_id"],
                    plays=[dict(card_index=untargeted["index"], card_id=untargeted["id"], target=target)],
                    target_hp_range=dict(target=target, min=hp, max=start))
    assert rejected["stop_reason"] == "target_mismatch" and rejected["submitted"] == [], rejected
    plan = dict(plays=[dict(card_index=card["index"], card_id=card["id"], target=target)],
                turn_id=first["host"]["battle"]["sequence_turn_id"], view="decision")
    for extra in ({}, {"target_hp_range": dict(target=target, min=start, max=start)}):
        receipt = call("host", "mp_combat_play_sequence", **plan, **extra)
        assert receipt["stop_reason"] == "enemies_changed" and receipt["submitted"] == [] and receipt["next_tool"]
        assert receipt["changes"] == [dict(entity_id=target, field="hp", before=start, after=hp)], receipt["changes"]
    receipt = call("host", "mp_combat_play_sequence", **plan, target_hp_range=dict(target=target, min=hp, max=start))
    assert receipt["status"] == "ok" and receipt["submitted"] == [card["id"]], str(receipt)[:400]
    # Hold two seats in this round so the guest vote and undo remain observable.
    g = combat("guest2")
    c = next(c for c in g["player"]["hand"] if c["can_play"])
    play = dict(card_index=c["index"],card_id=c["id"])
    if c.get("legal_enemy_ids"):
        play["target"] = c["legal_enemy_ids"][0]
    result = call("guest2", "mp_combat_play_sequence", turn_id=g["battle"]["sequence_turn_id"],
                  plays=[play], end_turn=True, view="decision")
    if not result["end_turn_submitted"]:
        assert call("guest2", "mp_combat_end_turn", allow_playable_cards=True)["turn_status"] == "end_submitted"
    else:
        battle = result["state"]["battle"]
        assert not battle["can_act"] and "sequence_turn_id" not in battle
        local = next(p for p in result["state"]["players"] if p["is_local"])
        assert battle["end_turn_pending"] or local["is_ready_to_end_turn"]
    ended = until(lambda: call("guest2", "mp_combat_decision"), lambda v: v["battle"]["can_undo_end_turn"])
    assert not ended["battle"]["can_act"] and "sequence_turn_id" not in ended["battle"]
    assert call("guest2", "mp_combat_undo_end_turn")["status"] == "ok"
    until(lambda: combat("guest2"), lambda v: v["battle"]["can_act"])
    cursor = first["host"]["coordination"]["latest_id"]
    message = "Next round, hold attacks until the host plays its debuff."
    assert call("guest2", "mp_coordination_message", all_players=True, text=message)["status"] == "ok"
    received = call("host", "mp_coordination_state", after_id=cursor, wait_seconds=4)
    assert any(e["text"] == message for e in received["events"])
    with ThreadPoolExecutor() as executor:
        votes = list(executor.map(lambda s: call(s, "mp_combat_end_turn", allow_playable_cards=True), seats))
    for s, vote in zip(seats, votes):
        if vote["status"] == "error":
            # A vote can collide with another seat's enqueue. Only repeat an
            # explicitly rejected input, and check its turn before resubmission.
            assert vote.get("retryable") and vote["reason"] in {"hand_not_ready", "action_resolving"}, vote
            current = combat(s)
            assert current["battle"]["round"] == 1 and current["battle"]["can_act"]
            vote = call(s, "mp_combat_end_turn", allow_playable_cards=True)
        assert vote["status"] == "ok", vote
    for s in seats:
        until(lambda: combat(s), lambda v: v["battle"]["round"] == 2 and v["battle"]["can_act"])
    report["hp_change"] = [start, hp]


for s in seats:
    until(lambda: (run / s / "ready.json").exists(), bool, 35)
    if case.startswith("treasure") and not case.endswith("control"):
        def read_chest():
            return call(s, "mp_combat_decision").get("treasure", {}).get("status")
        until(read_chest, lambda status: status == "closed", 40)
        assert read_chest() == "closed"
    else:
        until(lambda: state(s), lambda v: v.get("run", {}).get("floor"), 40)
if case.startswith("treasure"):
    treasure(case.endswith("control"))
else:
    {"event": event, "shop": shop, "race": race}[case]()
report["passed"] = case
(run / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(report, flush=True)
