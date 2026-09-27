# Battery outage tutorial

Current local BaseHack worktree, main at ba6f490 plus current changes. Uses the existing Home Assistant + PanelBench lab; no branch or paired service checkout is needed. This is a user tutorial, not a complete PR coverage review.

| Behavior | Visible proof | Clip |
| --- | --- | --- |
| Start utility outage | Grid outage enabled, Backup ready on, zero household power | 1 |
| Restore selected circuit | Laundry circuit power on | 1 |
| Start a load on battery | Dryer on, approximately 5 kW battery output, zero grid import | 1 |
| Budget rejection | Garage restoration refused while dryer reserves 5,001 W | 1 |
| Release capacity | Laundry breaker off, reservation removed | 1 |
| Finish scenario | Grid outage off, supplies restored, quiet-house reset | 1 |

Capture actual browser UI actions. Add instructional captions in post-production. Remove idle setup waits with explicit waiting captions; keep all initiating actions and their outcomes. Deliver GIF and a smaller MP4 companion, each under five minutes.

## Verified result

- GIF: 72 seconds, 1280 × 832, 864 frames, 5.1 MB.
- MP4 companion: same complete walkthrough; captions preserve the real recorded UI.
- Inspected decoded GIF frames from all 12 scenes, including battery output at 5,000 W / grid import at 0 W, budget refusal, and final grid-connected state.
- Final Home Assistant state: outage off, all eight circuit supplies on, battery output 0 W, quiet-house load 365 W.
- An idle MQTT heartbeat issue found during capture was fixed; twenty seconds at zero load remained connected. Python compilation and git diff whitespace checks passed.
- This recording covers the requested outage tutorial only, not the unrelated current worktree changes.

| Time | Action |
| --- | --- |
| 0:00 | Start grid outage |
| 0:05 | Wait for Backup ready |
| 0:09 | Restore Laundry circuit |
| 0:16 | Turn on dryer |
| 0:23 | Verify battery and grid readings |
| 0:28 | Attempt oversized Garage restore |
| 0:36 | Inspect budget rejection |
| 0:42 | Turn Laundry breaker off to free capacity |
| 0:49 | End grid outage |
| 0:56 | Restore circuit supplies |
| 1:01 | Reset realistic household |
| 1:08 | Confirm ready to run again |
