"""Run with the MCP client's Python environment."""
import importlib.util
from pathlib import Path
import unittest

import httpx


class CursorTest(unittest.TestCase):
    def test_overlapping_reads_and_game_restart(self):
        path = Path(__file__).resolve().parents[2] / "mcp" / "server.py"
        spec = importlib.util.spec_from_file_location("sts2_cursor_test", path)
        server = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(server)

        def deliver(request, session, cursor, markdown=False):
            headers = {"X-STS2-Coordination-Session": session}
            if markdown:
                headers["X-STS2-Coordination-Cursor"] = str(cursor)
                reply = httpx.Response(200, text="# Co-op chat", headers=headers)
            else:
                reply = httpx.Response(200, json={"coordination": {"latest_id": cursor}}, headers=headers)
            self.assertEqual(server._delivered_response(reply, request), reply.text)
            return server._coordination_after(None, 30)

        self.assertEqual(deliver(2, "first", 5, markdown=True), 5)
        self.assertEqual(deliver(1, "first", 6), 6)
        self.assertEqual(deliver(3, "first", 5), 6)
        self.assertEqual(deliver(5, "second", 1), 1)
        self.assertEqual(deliver(4, "first", 7), 1)


if __name__ == "__main__":
    unittest.main()
