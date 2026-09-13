"""Inject recorded sim data into docs/index.html at the /*__SIMDATA__*/ marker.
Idempotent: replaces the current inline data (or the empty marker) each run.

    python training/inject_simdata.py docs/sim_data.js docs/index.html
"""
import re
import sys

data_js = open(sys.argv[1]).read().strip()          # 'window.SIM_DATA={...};'
html_path = sys.argv[2]
html = open(html_path).read()

open_tag = "<!-- live-sim data (recorded rollout; injected by training/record_sim.py output) -->\n<script>"
close_tag = "</script>"
i = html.index(open_tag) + len(open_tag)
j = html.index(close_tag, i)
new = html[:i] + data_js + html[j:]
open(html_path, "w").write(new)
print(f"injected {len(data_js)/1024:.0f} KB into {html_path}")
