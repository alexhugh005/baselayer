# Portable Energy Lab validation

Validated on Docker Desktop / Apple Silicon on 2026-09-26.

- Built Home Assistant and PanelBench from pinned public upstream sources.
- Started a new Compose project on unused localhost ports and empty named volumes.
- Verified first-run HA onboarding and creation of a new owner account.
- Verified the generated bridge credential reads states and rejects device commands.
- Verified exactly one paired SPAN panel with TLS verification and stable Docker DNS.
- Verified all eight circuit supplies, live household power, lamp stability across
  the periodic interlock, breaker shutoff, blocked restart without supply, and manual recovery.
- Recreated both containers and verified owner authentication, saved battery charge,
  unchanged one-time initialization marker, pairing and device controls.
- Confirmed startup-default helpers retain their documented reset behavior.
- Verified seeding preserves later edits and refuses an existing unrecognized HA volume.
- Extracted the distribution ZIP outside the repository and built both images from it.
- Checked the ZIP for personal backup files, auth storage, local runtime files and tokens;
  none are included. Checked ZIP integrity and Python syntax.

A restart-test device check was interrupted by host clamshell sleep. macOS power logs
confirmed the sleep, and the bridge correctly disconnected and shut off loads.
The same acceptance test passed with the host awake. No simulator bypass was added.

The disposable test stack was removed after validation; the original lab was not changed.
The portable stack contains Home Assistant and PanelBench, not the Base Layer API/frontend.

Archive SHA-256: `48c7554b32d18954bd7ad8b4cf6a27325c77cd842dd2d31b851a87c0e18d725c`
