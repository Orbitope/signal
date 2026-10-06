<!-- leyline:begin -->
# Change brief: Emergency vehicle preemption

Signals treat every vehicle alike. A fire engine waits at a red like any car. Preemption is the standard answer: a signal that sees an emergency vehicle coming gives its approach a green.

## 1. What code will be written

| Task | Does | Code |
| --- | --- | --- |
| 1.1 | Add `Vehicle.IsEmergency`, false by default | Vehicle.IsEmergency |
| 1.2 | Add `DemandSource.EmergencyQueues`, one queue per origin, created on first use | DemandSource.EmergencyQueues |
| 1.3 | Add `DemandSource.SpawnEmergency`, which routes an emergency vehicle and puts it in the emergency queue for its origin, or returns null when there is no route | DemandSource.SpawnEmergency |
| 1.4 | Change `DemandSource.Tick` to release the head of each emergency queue before the ordinary queue for that origin | DemandSource.Tick |
| 1.5 | Change `DemandSource.HeldCount` to count vehicles in the emergency queues too | DemandSource.HeldCount |
| 1.6 | Add `Simulation.EmergencyCount`, raised by `Simulation.SpawnEmergency` and lowered in `Simulation.TryTransfer` where an emergency vehicle leaves the network | Simulation.EmergencyCount, Simulation.SpawnEmergency, Simulation.TryTransfer |
| 1.7 | Add `Simulation.SpawnEmergency`, which calls the demand source and returns the vehicle or null | Simulation.SpawnEmergency |
| 1.8 | Change `DemandSource.AccumulateHeldWait` to include vehicles in the emergency queues | DemandSource.AccumulateHeldWait |
| 2.1 | Add `SignalController.PreemptDistance`, 250 metres by default; zero switches preemption off | SignalController.PreemptDistance |
| 2.2 | Add `SignalController.Preempting`, true while an emergency vehicle is within the distance on an incoming link | SignalController.Preempting |
| 2.3 | Add `SignalController.PreemptPhase`, which returns the phase to serve as described in design.md, or -1 | SignalController.PreemptPhase |
| 2.4 | Change `SignalController.Tick` to check for preemption every tick when the emergency count is above zero, before the policy is asked; when the preempt phase is -1 the policy is asked as today | SignalController.Tick |
| 2.5 | Change `SignalController.RequestPhase` to ignore requests from outside while preempting | SignalController.RequestPhase |
| 3.1 | Add the test "preemption lets an emergency vehicle through a signal held red" to `Signal.Tests/Program.cs` | a test for the scenario, in Signal.Tests/Program.cs |
| 3.2 | Add the test "preemption ends once the emergency vehicle has passed" | a test for the scenario |
| 3.3 | Add the test "preemption overrules requests made while it is in force" | a test for the scenario |
| 3.4 | Add the test "signals with no emergency vehicle nearby keep following their policy" | a test for the scenario |
| 3.5 | Add the test "preemption keeps min-green, yellow and all-red" | a test for the scenario |
| 3.6 | Add the test "an emergency vehicle enters ahead of cars held at a backed-up entrance" | a test for the scenario |
| 3.7 | Add the test "spawning an emergency vehicle with no route returns null and changes nothing" | a test for the scenario |
| 3.8 | Add the test "a run with no emergency vehicle has the same state hash as before the change" | a test for the scenario |
| 3.9 | Add the test "an emergency vehicle in the network keeps at least 75% of the step rate" | a test for the scenario |
| 3.10 | Add the test "a held emergency vehicle counts toward the average wait" | a test for the scenario |

## 2. What it will affect

14 things change, 0 more must be edited with them, and 63 are reached without needing an edit, across 8 modules. 22 existing tests pass through the change.
- Signal.Core: 14 changed, 9 reached
- godot: 18 reached
- Signal.Tests: 16 reached
- training: 11 reached
- Signal.Headless: 4 reached
- Signal.EnvServer: 2 reached
- tools: 2 reached
- Signal.Scaling: 1 reached
- **medium risk:** Reaches outside its own module: Signal.EnvServer, Signal.Headless, Signal.Scaling, godot, tools, training.
- **low risk:** 28 direct caller links are guesses by name, so the caller list may be wrong.

**Uses the same things, and no task names it.** Each line is right to leave alone or a missing task:
- DemandSource.EmergencyQueues (new) sits beside DemandSource.EntryQueues, which is used by AgentView.TickPressure, AgentView.WriteObs, Snap
- Simulation.EmergencyCount (new) sits beside Simulation.StepCount, which is used by Simulation.StateHash, Simulation.Step, HeadlessSmoke._Ready
- SignalController.PreemptPhase (new) sits beside SignalController.CurrentPhase, which is used by AgentView.WriteMask, AgentView.WriteObs, AgingMaxPressurePolicy.SelectPhase, GreedyPolicy.SelectPhase, MaxPressurePolicy.SelectPhase, SignalController.EnterPhase and 5 more
- SignalController.RequestPhase is also called by AgentView.Act, SimRunner.RequestGreenFor
- DemandSource.Tick is also called by Simulation.Step
- SignalController.TimeInPhase (used by SignalController.Tick) is also used by AgentView.WriteMask, AgentView.WriteObs, SignalController.EnterPhase, <top-level> and 1 more
- SignalController._pendingPhase (used by SignalController.Tick) is also used by SignalController.SetPending
- SignalController.TimeInState (used by SignalController.Tick) is also used by SignalController.EnterPhase
- and 23 more: `leyline spec facts`

**Design it sits in:**
- observer: Simulation raises VehicleDespawned; 4 handlers elsewhere subscribe to it, so the raiser does not know who listens.
- observer: Simulation raises SpillbackStarted; 1 handler elsewhere subscribes to it, so the raiser does not know who listens.
- observer: Simulation raises VehicleSpawned; 2 handlers elsewhere subscribe to it, so the raiser does not know who listens.
- strategy: ISignalPolicy has 7 implementations (AgingMaxPressurePolicy, ExternalPolicy, FixedTimePolicy, GreedyPolicy, ...). SignalController holds one and calls it without knowing which.

## 3. How you will know it was done

| Scenario | When | Then | Test |
| --- | --- | --- | --- |
| preemption lets an emergency vehicle through a signal held red | a policy holds the cross street green and an emergency vehicle is spawned on the other street | the emergency vehicle leaves the network; in the same run with the preemption distance set to zero it is still waiting at the stop line | exists |
| preemption ends once the emergency vehicle has passed | the emergency vehicle has left the network | the emergency count is zero, the controller is not preempting, and an outside phase request is honored again | exists |
| preemption overrules requests made while it is in force | an outside caller requests another phase while the controller is preempting | the controller still serves the emergency vehicle's phase | exists |
| signals with no emergency vehicle nearby keep following their policy | an emergency vehicle approaches the first signal of a three-signal arterial | the last signal, with no emergency vehicle on its incoming links, still changes phase as its policy asks | exists |
| preemption keeps min-green, yellow and all-red | emergency vehicles arrive from alternating streets | no green is shorter than the minimum green and every change passes through yellow and all-red | exists |
| an emergency vehicle enters ahead of cars held at a backed-up entrance | cars are held at an entrance and an emergency vehicle is spawned there | the emergency vehicle enters the first link before any held car | exists |
| a held emergency vehicle counts toward the average wait | an emergency vehicle is spawned and has not yet entered its first link | the held-wait total counts one vehicle more than before it was spawned | exists |
| spawning an emergency vehicle with no route returns null and changes nothing | an emergency vehicle is requested between two nodes with no route | the result is null and the emergency count is unchanged | exists |
| a run with no emergency vehicle has the same state hash as before the change | the four-way signalized level with symmetric demand of 20 runs 2,000 steps on seed 7 with no policy | its state hash is 1821680719510123714, the value recorded on main at commit 313ea68 | exists |
| an emergency vehicle in the network keeps at least 75% of the step rate | the three-signal arterial runs with an emergency vehicle always present, respawned each time one leaves | it runs at 75% or more of the steps per second of the same run with none | exists |

## Review findings

9 raised, all settled (full text: `leyline spec findings`):
- accepted, high: Task 2.4 now falls back to the policy when the preempt phase is -1; scenario added for signals with no emergency vehicle nearby.
- accepted, high: Scenario replaced by one that fails without the feature: the same run with the distance at zero leaves the vehicle waiting.
- accepted, high: Matthew: preemption wins.
- accepted, medium: Matthew: keep minimum green, check every tick, look 250 m ahead.
- accepted, medium: Hash scenario now names the level, seed, steps and the value recorded on main.
- accepted, medium: Matthew: front of the queue.
- accepted, medium: design.md states how the phase is picked, the tie rule and the short-link limit.
- accepted, medium: Speed scenario added with an emergency vehicle always present.
- accepted, low: design.md says the scan runs every tick and accepts the cost; the speed scenario bounds it.

## Before implementation

Nothing blocks implementation: every task is tied to code, every must-edit is covered, and every scenario has a test or is marked to be written.

## 4. Was it done as agreed

**Yes.** Every task is done, every scenario is proven, and nothing outside the spec changed.

| Task | Result | Missing |
| --- | --- | --- |
| 1.1 Add `Vehicle.IsEmergency`, false by default | done |  |
| 1.2 Add `DemandSource.EmergencyQueues`, one queue per origin, created on f | done |  |
| 1.3 Add `DemandSource.SpawnEmergency`, which routes an emergency vehicle a | done |  |
| 1.4 Change `DemandSource.Tick` to release the head of each emergency queue | done |  |
| 1.5 Change `DemandSource.HeldCount` to count vehicles in the emergency que | done |  |
| 1.6 Add `Simulation.EmergencyCount`, raised by `Simulation.SpawnEmergency` | done |  |
| 1.7 Add `Simulation.SpawnEmergency`, which calls the demand source and ret | done |  |
| 1.8 Change `DemandSource.AccumulateHeldWait` to include vehicles in the em | done |  |
| 2.1 Add `SignalController.PreemptDistance`, 250 metres by default; zero sw | done |  |
| 2.2 Add `SignalController.Preempting`, true while an emergency vehicle is  | done |  |
| 2.3 Add `SignalController.PreemptPhase`, which returns the phase to serve  | done |  |
| 2.4 Change `SignalController.Tick` to check for preemption every tick when | done |  |
| 2.5 Change `SignalController.RequestPhase` to ignore requests from outside | done |  |
| 3.1 Add the test "preemption lets an emergency vehicle through a signal he | done |  |
| 3.10 Add the test "a held emergency vehicle counts toward the average wait" | done |  |
| 3.2 Add the test "preemption ends once the emergency vehicle has passed" | done |  |
| 3.3 Add the test "preemption overrules requests made while it is in force" | done |  |
| 3.4 Add the test "signals with no emergency vehicle nearby keep following  | done |  |
| 3.5 Add the test "preemption keeps min-green, yellow and all-red" | done |  |
| 3.6 Add the test "an emergency vehicle enters ahead of cars held at a back | done |  |
| 3.7 Add the test "spawning an emergency vehicle with no route returns null | done |  |
| 3.8 Add the test "a run with no emergency vehicle has the same state hash  | done |  |
| 3.9 Add the test "an emergency vehicle in the network keeps at least 75% o | done |  |

| Scenario | Result | Evidence |
| --- | --- | --- |
| preemption lets an emergency vehicle through a signal held red | passes | its test reaches the changed code on the map |
| preemption ends once the emergency vehicle has passed | passes | its test reaches the changed code on the map |
| preemption overrules requests made while it is in force | passes | its test reaches the changed code on the map |
| signals with no emergency vehicle nearby keep following their policy | passes | its test reaches the changed code on the map |
| preemption keeps min-green, yellow and all-red | passes | its test reaches the changed code on the map |
| an emergency vehicle enters ahead of cars held at a backed-up entrance | passes | its test reaches the changed code on the map |
| a held emergency vehicle counts toward the average wait | passes | its test reaches the changed code on the map |
| spawning an emergency vehicle with no route returns null and changes nothing | passes | its test reaches the changed code on the map |
| a run with no emergency vehicle has the same state hash as before the change | passes | its test reaches the changed code on the map |
| an emergency vehicle in the network keeps at least 75% of the step rate | passes | its test reaches the changed code on the map |

**Helpers added** (new, and called only by code the spec names):
- SignalController.SetPending, called by SignalController.RequestPhase, SignalController.Tick

Tests: 24 of 24 passed before, 34 of 34 after.
Review: 9 findings, 0 still open.
Compared with the code as it was at 313ea68.
<!-- leyline:end -->
