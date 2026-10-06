<!-- leyline:begin -->
# Change brief: Emergency vehicle preemption

Signals treat every vehicle alike. A fire engine waits at a red like any car. Preemption is the standard answer: a signal that sees an emergency vehicle coming gives its approach a green.

## 1. What code will be written

| Task | Does | Code |
| --- | --- | --- |
| 1.1 | Add `Vehicle.IsEmergency`, false by default | Vehicle.IsEmergency (new) |
| 1.2 | Add `DemandSource.EmergencyQueues`, one queue per origin, created on first use | DemandSource.EmergencyQueues (new) |
| 1.3 | Add `DemandSource.SpawnEmergency`, which routes an emergency vehicle and puts it in the emergency queue for its origin, or returns null when there is no route | DemandSource.SpawnEmergency (new) |
| 1.4 | Change `DemandSource.Tick` to release the head of each emergency queue before the ordinary queue for that origin | DemandSource.Tick |
| 1.5 | Change `DemandSource.HeldCount` to count vehicles in the emergency queues too | DemandSource.HeldCount |
| 1.6 | Add `Simulation.EmergencyCount`, raised by `Simulation.SpawnEmergency` and lowered in `Simulation.TryTransfer` where an emergency vehicle leaves the network | Simulation.TryTransfer, Simulation.EmergencyCount (new), Simulation.SpawnEmergency (new) |
| 1.7 | Add `Simulation.SpawnEmergency`, which calls the demand source and returns the vehicle or null | Simulation.SpawnEmergency (new) |
| 2.1 | Add `SignalController.PreemptDistance`, 250 metres by default; zero switches preemption off | SignalController.PreemptDistance (new) |
| 2.2 | Add `SignalController.Preempting`, true while an emergency vehicle is within the distance on an incoming link | SignalController.Preempting (new) |
| 2.3 | Add `SignalController.PreemptPhase`, which returns the phase to serve as described in design.md, or -1 | SignalController.PreemptPhase (new) |
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

## 2. What it will affect

14 things change, 0 more must be edited with them, and 46 are reached without needing an edit, across 8 modules. 12 existing tests pass through the change.
- Signal.Core: 5 changed, 7 reached
- godot: 15 reached
- training: 11 reached
- Signal.Tests: 4 reached
- Signal.Headless: 4 reached
- Signal.EnvServer: 2 reached
- tools: 2 reached
- Signal.Scaling: 1 reached
- **medium risk:** Reaches outside its own module: Signal.EnvServer, Signal.Headless, Signal.Scaling, Signal.Tests, godot, tools, training.
- **low risk:** 8 direct caller links are guesses by name, so the caller list may be wrong.

**Shared state it writes:**
- Vehicle.Pos, also assigned from DemandSource, Simulation
- Vehicle.Speed, also assigned from DemandSource, Simulation
- Vehicle.Wait, also assigned from DemandSource, Simulation

**Design it sits in:**
- observer: Simulation raises VehicleDespawned; 1 handler elsewhere subscribes to it, so the raiser does not know who listens.
- observer: Simulation raises SpillbackStarted; 1 handler elsewhere subscribes to it, so the raiser does not know who listens.
- observer: Simulation raises VehicleSpawned; 1 handler elsewhere subscribes to it, so the raiser does not know who listens.
- strategy: ISignalPolicy has 7 implementations (AgingMaxPressurePolicy, ExternalPolicy, FixedTimePolicy, GreedyPolicy, ...). SignalController holds one and calls it without knowing which.

## 3. How you will know it was done

| Scenario | When | Then | Test |
| --- | --- | --- | --- |
| preemption lets an emergency vehicle through a signal held red | a policy holds the cross street green and an emergency vehicle is spawned on the other street | the emergency vehicle leaves the network; in the same run with the preemption distance set to zero it is still waiting at the stop line | to be written, with this name |
| preemption ends once the emergency vehicle has passed | the emergency vehicle has left the network | the emergency count is zero, the controller is not preempting, and an outside phase request is honored again | to be written, with this name |
| preemption overrules requests made while it is in force | an outside caller requests another phase while the controller is preempting | the controller still serves the emergency vehicle's phase | to be written, with this name |
| signals with no emergency vehicle nearby keep following their policy | an emergency vehicle approaches the first signal of a three-signal arterial | the last signal, with no emergency vehicle on its incoming links, still changes phase as its policy asks | to be written, with this name |
| preemption keeps min-green, yellow and all-red | emergency vehicles arrive from alternating streets | no green is shorter than the minimum green and every change passes through yellow and all-red | to be written, with this name |
| an emergency vehicle enters ahead of cars held at a backed-up entrance | cars are held at an entrance and an emergency vehicle is spawned there | the emergency vehicle enters the first link before any held car | to be written, with this name |
| spawning an emergency vehicle with no route returns null and changes nothing | an emergency vehicle is requested between two nodes with no route | the result is null and the emergency count is unchanged | to be written, with this name |
| a run with no emergency vehicle has the same state hash as before the change | the four-way signalized level with symmetric demand of 20 runs 2,000 steps on seed 7 with no policy | its state hash is 1821680719510123714, the value recorded on main at commit 313ea68 | to be written, with this name |
| an emergency vehicle in the network keeps at least 75% of the step rate | the three-signal arterial runs with an emergency vehicle always present, respawned each time one leaves | it runs at 75% or more of the steps per second of the same run with none | to be written, with this name |

## Review findings

- **accepted, high** (logic, f-46cdd6): Task 2.4 gates on the network-wide Simulation.EmergencyCount and requests PreemptPhase 'in place of asking the policy', so every signal that has no emergency vehicle nearby gets PreemptPhase = -1, RequestPhase(-1) returns without doing anything, and its policy is never consulted: all other signals in the network freeze on their current phase for as long as one emergency vehicle exists anywhere (including after it has passed its last signal and until it exits). Proposed: Reword 2.4: when EmergencyCount > 0, compute PreemptPhase; if it is >= 0 request it, otherwise fall through to the policy exactly as today. Add a scenario: with an emergency vehicle in a multi-signal network, a signal it is not approaching still follows its policy; and preemption ends when the vehicle crosses that signal's stop line, not when it leaves the network. Resolution: Task 2.4 now falls back to the policy when the preempt phase is -1; scenario added for signals with no emergency vehicle nearby.
- **accepted, high** (logic, f-e3752b): The scenario 'green before the vehicle reaches the end of its link' is true with no preemption at all: a vehicle facing a red brakes for the line in ResolveFront and TryTransfer clamps it to Length - 0.01, so no vehicle ever reaches the end of its link before its movement is green, and test 3.1 would pass against today's code with any policy that eventually serves the approach. Proposed: Replace the THEN with something only preemption can satisfy, for example: with a policy that never selects the serving phase (ExternalPolicy pinned to the other phase), the serving phase is green within MinGreen + yellow + all-red + one tick of the vehicle coming within PreemptDistance, and the vehicle leaves the network; the same run without IsEmergency leaves it waiting. Resolution: Scenario replaced by one that fails without the feature: the same run with the distance at zero leaves the vehicle waiting.
- **accepted, high** (logic, f-c3bb75): Two callers set the pending phase directly, outside Tick: AgentView.Act (every env-server action) and SimRunner.RequestGreenFor (every player tap) both call SignalController.RequestPhase, so an RL action or a tap for another phase overwrites the preemption request and can turn the emergency vehicle's green to yellow; the spec has no task or rule for either. Proposed: State the precedence rule and add a task for it: while Preempting, RequestPhase from outside the controller is ignored (or recorded and applied after preemption ends), implemented inside SignalController so both callers are covered. Add a scenario: an external request for a conflicting phase during preemption does not change the phase. Say in the proposal that the env server's actions and the player's taps are overruled during preemption (the tap's 12 s hold keeps counting down), since 'protocol does not change' hides that agent actions silently stop having effect. Resolution: Matthew: preemption wins. RequestPhase ignores outside requests while preempting (task 2.5), with a scenario.
- **accepted, medium** (logic, f-5f83c3): Today the policy is consulted only when Policy != null, State == Green and the decision timer (0.5 s default, set to 5 s by the tests and the game AI) has expired; the spec does not say whether preemption sits inside that gate, and if it does, a 150 m approach at the 13.9 m/s default limit (10.8 s) cannot be served after up to 5 s decision delay plus the default 5 s min green, 3 s yellow and 1 s all-red, and a controller with no policy never preempts. Proposed: Specify in 2.4 that the preemption check runs every tick, in every signal state, and whether or not a policy is attached; say what happens when it starts during yellow or all-red toward another phase (the pending phase is replaced, so the next green is the preempt phase). Either justify 150 m against min green + yellow + all-red at the speed limit or state that the vehicle may still have to stop. Resolution: Matthew: keep minimum green, check every tick, look 250 m ahead.
- **accepted, medium** (logic, f-fe6f56): The scenario 'state hash equals the hash of the same run on the code before this change' cannot be run by the test runner: Signal.Tests has no stored golden hash (the determinism test compares two runs of the same build), no task captures one before the change, and the scenario does not name the level, seed or policy; StateHash also omits the pending phase and any new fields, so it would not see a preemption request that had not yet changed a phase. Proposed: Add a task before 1.1: record StateHash after 2,000 steps for a named level, seed and policy (e.g. FourWay signalized, 30 veh/min, seed 42, FixedTimePolicy 30 s) on the current commit and commit it as a constant; make scenario 4 compare against that constant. Add an assertion that Preempting was never true on any controller during the run. Resolution: Hash scenario now names the level, seed, steps and the value recorded on main.
- **accepted, medium** (logic, f-7016b5): DemandSource.EntryQueues only has a queue for nodes that are the origin of a configured flow, and the queue is strict FIFO released one head per tick when the entry link has space, so SpawnEmergency 'puts it in the entry queue' throws KeyNotFound for an origin with no flow (e.g. a test level with an empty DemandDef), waits behind every held car at a backed-up gate, and Router.Route can return null; the spec defines none of these, and EmergencyCount (raised in OnVehicleSpawned) does not count a vehicle still held at the gate. Proposed: In 1.2 say: create the entry queue if the origin has none; return false (or throw a named exception) when there is no route or the origin is not a boundary node; and decide whether an emergency vehicle joins the back of the gate queue or the front. Add scenarios for the unroutable request and for an origin with no flow, and state that the vehicle id comes from the same counter without drawing from the Rng. Resolution: Matthew: front of the queue. Separate emergency queues, created on first use; null when no route; two scenarios.
- **accepted, medium** (logic, f-96b921): 'Within the preemption distance on one of its approaches' is undefined when the signal's in-link is shorter than 150 m: in FourWayWithBays the in-links of the signal are 60 m bay and lane links fed by a 90 m arm link through a fork, so an emergency vehicle 61 to 150 m away is on a link that is not an in-link of the controller, and 'the phase that serves' it is also undefined when its movement is only permissive (the left in TwoPhasePermissiveLefts still gap-accepts against opposing through traffic) or when two emergency vehicles are equally near on conflicting approaches. Proposed: Define distance in 2.2 as distance along the vehicle's remaining route to this node's stop line, covering upstream links (or state plainly that only the final in-link counts and the effective distance is min(150, in-link length)). Define the phase choice: the current phase if it serves the movement, else the lowest-index phase where it is protected, else the lowest-index phase containing it; ties between vehicles go to the lower in-link id. Add a scenario on FourWayWithBays and one with two emergency vehicles on conflicting approaches at once. Resolution: design.md states how the phase is picked, the tie rule and the short-link limit.
- **accepted, medium** (performance, f-0a52a6): No test would show a slowdown from this change: the only speed test ('>= 10k steps/sec on the 3-intersection arterial') runs 3 fixed-time signals with no emergency vehicle and passes at 10,000 steps/sec while the code measures about 71,500 today, so a 7x regression passes, and tasks 3.1-3.4 add no speed test for the case where an emergency vehicle exists. Proposed: Add task 3.5: a speed test that runs the same network and seed twice, once with no emergency vehicle and once with one kept in the network for the whole run (respawned on exit), on a network with many signals (for example GridBuilder.Grid(5) as Signal.Scaling uses), and fails if the second run is more than a stated fraction slower (for example 10%). Record the pre-change baseline (71,562 steps/sec on the arterial test in Release) in design.md so the no-emergency path can be compared after implementation. Resolution: Speed scenario added with an emergency vehicle always present.
- **accepted, low** (performance, f-43ea90): Task 2.4 and the design bound the cost only when EmergencyCount is zero: the count is network-wide, so one emergency vehicle anywhere makes every SignalController run PreemptPhase, and the spec does not say whether that runs every tick or under the existing DecisionInterval timer (0.5 s by default, 5 s in Signal.Scaling) that today gates the only per-controller scan (Policy.SelectPhase, Green state only); emulating a 150 m approach scan in every controller on the 5x5 grid cost about 4-12% of steps/sec every tick and about 0-4% every 5th tick. Proposed: State in design.md what a tick costs while an emergency vehicle exists, and choose one: (a) task 2.4 runs PreemptPhase only inside the existing decision-timer block, or (b) replace the bare count in task 1.4 with a list of live emergency vehicles on Simulation, so PreemptPhase checks only those vehicles (current link ends at this node and Length - Pos <= PreemptDistance) and costs O(emergency vehicles) per controller, not O(vehicles on its approaches). In either case require task 2.2 to allocate nothing per call and, if it scans a link, to stop at the first vehicle farther than PreemptDistance (Link.Vehicles is front-first). Resolution: design.md says the scan runs every tick and accepts the cost; the speed scenario bounds it.

## Before implementation

Nothing blocks implementation: every task is tied to code, every must-edit is covered, and every scenario has a test or is marked to be written.

## 4. Was it done as agreed

**Not yet:** 1 edits are outside the spec.

| Task | Result | Missing |
| --- | --- | --- |
| 1.1 Add `Vehicle.IsEmergency`, false by default | done |  |
| 1.2 Add `DemandSource.EmergencyQueues`, one queue per origin, created on f | done |  |
| 1.3 Add `DemandSource.SpawnEmergency`, which routes an emergency vehicle a | done |  |
| 1.4 Change `DemandSource.Tick` to release the head of each emergency queue | done |  |
| 1.5 Change `DemandSource.HeldCount` to count vehicles in the emergency que | done |  |
| 1.6 Add `Simulation.EmergencyCount`, raised by `Simulation.SpawnEmergency` | done |  |
| 1.7 Add `Simulation.SpawnEmergency`, which calls the demand source and ret | done |  |
| 2.1 Add `SignalController.PreemptDistance`, 250 metres by default; zero sw | done |  |
| 2.2 Add `SignalController.Preempting`, true while an emergency vehicle is  | done |  |
| 2.3 Add `SignalController.PreemptPhase`, which returns the phase to serve  | done |  |
| 2.4 Change `SignalController.Tick` to check for preemption every tick when | done |  |
| 2.5 Change `SignalController.RequestPhase` to ignore requests from outside | done |  |
| 3.1 Add the test "preemption lets an emergency vehicle through a signal he | done |  |
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
| spawning an emergency vehicle with no route returns null and changes nothing | passes | its test reaches the changed code on the map |
| a run with no emergency vehicle has the same state hash as before the change | passes | its test reaches the changed code on the map |
| an emergency vehicle in the network keeps at least 75% of the step rate | passes | its test reaches the changed code on the map |

**Changed, but not in the spec:**
- SignalController.SetPending (new, not declared in the proposal) Signal.Core/Controls.cs

Tests: 24 of 24 passed before, 33 of 33 after.
<!-- leyline:end -->
