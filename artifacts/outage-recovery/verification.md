# Outage recovery verification

- Backend: 251 tests passed.
- Frontend: TypeScript build passed.
- Live local Home Assistant + PanelBench + Base Layer API: passed.
- Restored in order: Living Room (73 W), Essential Electronics (500 W), Office Outlets (1,516 W), Kitchen Outlets (2,553 W), Central HVAC (3,008 W).
- Five confirmed circuit commands; observed command gaps were 9.80, 10.05, 10.07, and 10.04 seconds (all at least the five-second demo minimum).
- Final battery reservation: 7,650 W, below the 10,500 W operating budget. Larger remaining circuits stayed off.
- Final cleanup: grid supply restored, all eight circuit supplies on, quiet household reset. API is running locally and monitoring for the next outage.
- Detailed transition evidence: live-verification.json.
