## 1. Vehicles and spawning
- [x] 1.1 Add `Vehicle.IsEmergency`, false by default
- [x] 1.2 Add `DemandSource.EmergencyQueues`, one queue per origin, created on first use
- [x] 1.3 Add `DemandSource.SpawnEmergency`, which routes an emergency vehicle and puts it in the emergency queue for its origin, or returns null when there is no route
- [x] 1.4 Change `DemandSource.Tick` to release the head of each emergency queue before the ordinary queue for that origin
- [x] 1.5 Change `DemandSource.HeldCount` to count vehicles in the emergency queues too
- [x] 1.6 Add `Simulation.EmergencyCount`, raised by `Simulation.SpawnEmergency` and lowered in `Simulation.TryTransfer` where an emergency vehicle leaves the network
- [x] 1.7 Add `Simulation.SpawnEmergency`, which calls the demand source and returns the vehicle or null

## 2. Controller
- [x] 2.1 Add `SignalController.PreemptDistance`, 250 metres by default; zero switches preemption off
- [x] 2.2 Add `SignalController.Preempting`, true while an emergency vehicle is within the distance on an incoming link
- [x] 2.3 Add `SignalController.PreemptPhase`, which returns the phase to serve as described in design.md, or -1
- [x] 2.4 Change `SignalController.Tick` to check for preemption every tick when the emergency count is above zero, before the policy is asked; when the preempt phase is -1 the policy is asked as today
- [x] 2.5 Change `SignalController.RequestPhase` to ignore requests from outside while preempting

## 3. Tests
- [x] 3.1 Add the test "preemption lets an emergency vehicle through a signal held red" to `Signal.Tests/Program.cs`
- [x] 3.2 Add the test "preemption ends once the emergency vehicle has passed"
- [x] 3.3 Add the test "preemption overrules requests made while it is in force"
- [x] 3.4 Add the test "signals with no emergency vehicle nearby keep following their policy"
- [x] 3.5 Add the test "preemption keeps min-green, yellow and all-red"
- [x] 3.6 Add the test "an emergency vehicle enters ahead of cars held at a backed-up entrance"
- [x] 3.7 Add the test "spawning an emergency vehicle with no route returns null and changes nothing"
- [x] 3.8 Add the test "a run with no emergency vehicle has the same state hash as before the change"
- [x] 3.9 Add the test "an emergency vehicle in the network keeps at least 75% of the step rate"
